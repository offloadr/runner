using System.Security.Cryptography;
using Offloadr.Runner.Core.Sealing;

namespace Offloadr.Runner.Tests;

public class SealedEnvelopeTests
{
    private const int ChunkSize = SealedEnvelopeFormat.ChunkSize;
    private const int Tag = SealedEnvelopeFormat.TagSize;

    private static readonly SealedArtifactIdentity Artifact = new("session-1", "editor-1", "output", "2026/10", "image_00001_.png");

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(65536)]
    [TestCase(65537)]
    [TestCase(131072)]
    public async Task RoundTrip_InArbitraryPieces_ReturnsThePlaintext(int size)
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var plaintext = RandomNumberGenerator.GetBytes(size);

        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, plaintext);
        var opened = await OpenAsync(envelope, recipient, Artifact, ciphertext, new Random(size));

        Assert.Multiple(() =>
        {
            Assert.That(ciphertext, Has.Length.EqualTo(SealedEnvelopeFormat.CiphertextSize(size)));
            Assert.That(opened, Is.EqualTo(plaintext));
        });
    }

    [TestCase(0, new[] { Tag })]
    [TestCase(1, new[] { 1 + Tag })]
    [TestCase(65536, new[] { ChunkSize + Tag })]
    [TestCase(65537, new[] { ChunkSize + Tag, 1 + Tag })]
    [TestCase(131072, new[] { ChunkSize + Tag, ChunkSize + Tag })]
    public async Task EncryptChunks_EndsWithOneFinalChunk_AndNoExtraEmptyChunk(int size, int[] chunkLengths)
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        using var writer = SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, size);

        var lengths = new List<int>();
        await foreach (var chunk in writer.EncryptChunksAsync(new MemoryStream(new byte[size])))
        {
            lengths.Add(chunk.Length);
        }

        Assert.That(lengths, Is.EqualTo(chunkLengths));
    }

    [Test]
    public async Task EncryptChunks_StreamsStraightIntoTheReader()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var plaintext = RandomNumberGenerator.GetBytes((2 * ChunkSize) + 5);
        using var writer = SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, plaintext.Length);
        var output = new MemoryStream();
        using var reader = SealedEnvelopeReader.Open(writer.Envelope, recipient, Artifact, output);

        await foreach (var chunk in writer.EncryptChunksAsync(new MemoryStream(plaintext)))
        {
            await reader.WriteAsync(chunk);
        }

        await reader.CompleteAsync();

        Assert.Multiple(() =>
        {
            Assert.That(output.ToArray(), Is.EqualTo(plaintext));
            Assert.That(reader.PlaintextBytesWritten, Is.EqualTo(plaintext.Length));
        });
    }

    [Test]
    public async Task EveryEncryption_UsesAFreshDataKeyAndEncapsulation()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var plaintext = new byte[100];

        var (first, firstCiphertext) = await SealAsync(recipient.PublicKey, Artifact, plaintext);
        var (second, secondCiphertext) = await SealAsync(recipient.PublicKey, Artifact, plaintext);

        Assert.Multiple(() =>
        {
            Assert.That(first.Header.ToArray(), Is.EqualTo(second.Header.ToArray()));
            Assert.That(first.EncapsulatedKey.ToArray(), Is.Not.EqualTo(second.EncapsulatedKey.ToArray()));
            Assert.That(first.SealedDataKey.ToArray(), Is.Not.EqualTo(second.SealedDataKey.ToArray()));
            Assert.That(firstCiphertext, Is.Not.EqualTo(secondCiphertext));
        });
    }

    [Test]
    public async Task Writer_EncryptsOnlyOnce()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        using var writer = SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, 1);
        await writer.EncryptToAsync(new MemoryStream([1]), new MemoryStream());

        Assert.That(
            () => writer.EncryptChunksAsync(new MemoryStream([1])),
            Throws.InstanceOf<InvalidOperationException>().Or.InstanceOf<ObjectDisposedException>());
    }

    [Test]
    public void Writer_RejectsPlaintextShorterThanDeclared()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        using var writer = SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, ChunkSize + 10);

        Assert.That(
            async () => await writer.EncryptToAsync(new MemoryStream(new byte[ChunkSize + 9]), new MemoryStream()),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("ended after"));
    }

    [Test]
    public void Writer_RejectsPlaintextLongerThanDeclared_BeforeTheFinalChunkLeaves()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        using var writer = SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, ChunkSize + 10);
        var chunks = new List<ReadOnlyMemory<byte>>();

        var failure = Assert.ThrowsAsync<SealedEnvelopeException>(async () =>
        {
            await foreach (var chunk in writer.EncryptChunksAsync(new MemoryStream(new byte[ChunkSize + 11])))
            {
                chunks.Add(chunk);
            }
        });

        Assert.Multiple(() =>
        {
            Assert.That(failure!.Message, Does.Contain("longer than"));
            Assert.That(chunks, Has.Count.EqualTo(1), "only the non-final chunk was released");
        });
    }

    [Test]
    public void Writer_RejectsPayloadsOverFourGiB()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, SealedEnvelopeFormat.MaxPlaintextSize + 1),
                Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(
                () => SealedEnvelopeWriter.Create(recipient.PublicKey, Artifact, -1),
                Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(
                SealedEnvelopeFormat.CiphertextSize(SealedEnvelopeFormat.MaxPlaintextSize),
                Is.EqualTo(SealedEnvelopeFormat.MaxPlaintextSize + (65536L * Tag)));
        });
    }

    [Test]
    public async Task Reader_RejectsATamperedChunk()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, RandomNumberGenerator.GetBytes(ChunkSize + 1));
        ciphertext[100] ^= 0x01;

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, new MemoryStream());

        Assert.That(
            async () => await reader.WriteAsync(ciphertext),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("chunk 0"));
    }

    [Test]
    public async Task Reader_RejectsTruncation_WhenTheFinalChunkIsDropped()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, RandomNumberGenerator.GetBytes(2 * ChunkSize));
        var output = new MemoryStream();

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, output);
        await reader.WriteAsync(ciphertext.AsMemory(0, ChunkSize + Tag));

        Assert.That(
            async () => await reader.CompleteAsync(),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("before the final chunk"));
    }

    [Test]
    public async Task Reader_RejectsTruncation_InsideAChunk()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, RandomNumberGenerator.GetBytes(10));

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, new MemoryStream());
        await reader.WriteAsync(ciphertext.AsMemory(0, ciphertext.Length - 1));

        Assert.That(async () => await reader.CompleteAsync(), Throws.InstanceOf<SealedEnvelopeException>());
    }

    [Test]
    public async Task Reader_RejectsReorderedChunks()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, RandomNumberGenerator.GetBytes(3 * ChunkSize));
        var reordered = new byte[ciphertext.Length];
        ciphertext.AsSpan(ChunkSize + Tag, ChunkSize + Tag).CopyTo(reordered);
        ciphertext.AsSpan(0, ChunkSize + Tag).CopyTo(reordered.AsSpan(ChunkSize + Tag));
        ciphertext.AsSpan(2 * (ChunkSize + Tag)).CopyTo(reordered.AsSpan(2 * (ChunkSize + Tag)));

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, new MemoryStream());

        Assert.That(async () => await reader.WriteAsync(reordered), Throws.InstanceOf<SealedEnvelopeException>());
    }

    [Test]
    public async Task Reader_RejectsTrailingBytesAfterTheFinalChunk()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, RandomNumberGenerator.GetBytes(ChunkSize));

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, new MemoryStream());
        await reader.WriteAsync(ciphertext);

        Assert.Multiple(() =>
        {
            Assert.That(
                async () => await reader.WriteAsync(new byte[] { 0 }),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("after the final chunk"));
            Assert.That(async () => await reader.CompleteAsync(), Throws.InstanceOf<SealedEnvelopeException>());
        });
    }

    [Test]
    public async Task Reader_RejectsAFinalChunkMarkedNonFinal()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var dataKey = RandomNumberGenerator.GetBytes(SealedEnvelopeFormat.DataKeySize);
        var envelope = SealDataKey(recipient.PublicKey, new SealedEnvelopeHeader("reader-1", 10, Artifact), dataKey);
        var chunk = EncryptChunk(dataKey, 0, final: false, new byte[10]);

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, new MemoryStream());

        Assert.That(async () => await reader.WriteAsync(chunk), Throws.InstanceOf<SealedEnvelopeException>());
    }

    [Test]
    public async Task Reader_RejectsAnEarlyChunkMarkedFinal()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var dataKey = RandomNumberGenerator.GetBytes(SealedEnvelopeFormat.DataKeySize);
        var envelope = SealDataKey(recipient.PublicKey, new SealedEnvelopeHeader("reader-1", ChunkSize + 1, Artifact), dataKey);
        var chunk = EncryptChunk(dataKey, 0, final: true, new byte[ChunkSize]);

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, new MemoryStream());

        Assert.That(async () => await reader.WriteAsync(chunk), Throws.InstanceOf<SealedEnvelopeException>());
    }

    [Test]
    public async Task Reader_WritesNothingFromAChunkThatFailsAuthentication()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, ciphertext) = await SealAsync(recipient.PublicKey, Artifact, RandomNumberGenerator.GetBytes(ChunkSize + 1));
        ciphertext[^1] ^= 0x01;
        var output = new MemoryStream();

        using var reader = SealedEnvelopeReader.Open(envelope, recipient, Artifact, output);

        Assert.That(async () => await reader.WriteAsync(ciphertext), Throws.InstanceOf<SealedEnvelopeException>());
        Assert.That(output.Length, Is.EqualTo(ChunkSize), "only the verified first chunk was written");
        Assert.That(async () => await reader.WriteAsync(Array.Empty<byte>()), Throws.InstanceOf<SealedEnvelopeException>());
    }

    [Test]
    public async Task Open_RejectsTheWrongPrivateKey()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        using var other = SealingKeyPair.Generate("reader-1");
        var (envelope, _) = await SealAsync(recipient.PublicKey, Artifact, new byte[5]);

        Assert.That(
            () => SealedEnvelopeReader.Open(envelope, other, Artifact, new MemoryStream()),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("could not be opened"));
    }

    [Test]
    public async Task Open_RejectsTheWrongKeyId()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, _) = await SealAsync(recipient.PublicKey, Artifact, new byte[5]);
        var header = SealedEnvelopeHeader.Parse(envelope.Header.Span) with { KeyId = "reader-2" };
        var relabelled = envelope with { Header = header.Encode() };
        using var relabelledRecipient = SealingKeyPair.Generate("reader-2");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SealedEnvelopeReader.Open(envelope, relabelledRecipient, Artifact, new MemoryStream()),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("different key id"));
            Assert.That(
                () => SealedEnvelopeReader.Open(relabelled, recipient, Artifact, new MemoryStream()),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("different key id"));
        });
    }

    [Test]
    public async Task Open_RejectsAnIdentityOtherThanTheOneRequested()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, _) = await SealAsync(recipient.PublicKey, Artifact, new byte[5]);

        Assert.Multiple(() =>
        {
            foreach (var requested in new SealedObjectIdentity[]
            {
                Artifact with { Filename = "image_00002_.png" },
                Artifact with { SessionId = "session-2" },
                Artifact with { Subfolder = "" },
                new SealedWorkspaceFileIdentity("session-1", "user", "image_00001_.png"),
            })
            {
                Assert.That(
                    () => SealedEnvelopeReader.Open(envelope, recipient, requested, new MemoryStream()),
                    Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("different object"));
            }
        });
    }

    [Test]
    public async Task Open_RejectsAHeaderChangedAfterSealing()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, _) = await SealAsync(recipient.PublicKey, Artifact, new byte[5]);
        var changed = envelope with
        {
            Header = (SealedEnvelopeHeader.Parse(envelope.Header.Span) with { PlaintextSize = 4 }).Encode(),
        };

        Assert.That(
            () => SealedEnvelopeReader.Open(changed, recipient, Artifact, new MemoryStream()),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("could not be opened"));
    }

    [Test]
    public async Task Open_RejectsAnInvalidEncapsulatedKey()
    {
        using var recipient = SealingKeyPair.Generate("reader-1");
        var (envelope, _) = await SealAsync(recipient.PublicKey, Artifact, new byte[5]);
        var offCurve = envelope.EncapsulatedKey.ToArray();
        offCurve[^1] ^= 0x01;

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SealedEnvelopeReader.Open(envelope with { EncapsulatedKey = offCurve }, recipient, Artifact, new MemoryStream()),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("not a point"));
            Assert.That(
                () => SealedEnvelopeReader.Open(envelope with { EncapsulatedKey = offCurve[..33] }, recipient, Artifact, new MemoryStream()),
                Throws.InstanceOf<SealedEnvelopeException>());
        });
    }

    [Test]
    public void PublicKey_RejectsInvalidPointsAndUnknownAlgorithms()
    {
        using var pair = SealingKeyPair.Generate("reader-1");
        var valid = pair.ExportPublicKey();
        var offCurve = (byte[])valid.Clone();
        offCurve[40] ^= 0x01;
        var compressed = new byte[33];
        compressed[0] = (byte)(2 + (valid[64] & 1));
        valid.AsSpan(1, 32).CopyTo(compressed.AsSpan(1));
        var outOfRange = (byte[])valid.Clone();
        outOfRange.AsSpan(1, 32).Fill(0xFF);
        var algorithm = SealedEnvelopeFormat.AlgorithmId;

        Assert.Multiple(() =>
        {
            Assert.That(SealingPublicKey.Create("reader-1", algorithm, valid).PublicKey.ToArray(), Is.EqualTo(valid));
            Assert.That(() => SealingPublicKey.Create("reader-1", algorithm, offCurve), Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(() => SealingPublicKey.Create("reader-1", algorithm, compressed), Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(() => SealingPublicKey.Create("reader-1", algorithm, outOfRange), Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(() => SealingPublicKey.Create("reader-1", algorithm, new byte[65]), Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(
                () => SealingPublicKey.Create("reader-1", "hpke-x25519-sha256-chacha20poly1305", valid),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("algorithm"));
            Assert.That(
                () => SealingPublicKey.Create("reader-1", algorithm.ToUpperInvariant(), valid),
                Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(() => SealingPublicKey.Create("", algorithm, valid), Throws.InstanceOf<SealedEnvelopeException>());
        });
    }

    [Test]
    public void KeyPair_RejectsUseAfterDispose()
    {
        var pair = SealingKeyPair.Generate("reader-1");
        pair.Dispose();
        pair.Dispose();

        Assert.That(() => pair.Key, Throws.InstanceOf<ObjectDisposedException>());
    }

    internal static async Task<(SealedEnvelope Envelope, byte[] Ciphertext)> SealAsync(
        SealingPublicKey recipient,
        SealedObjectIdentity identity,
        byte[] plaintext)
    {
        using var writer = SealedEnvelopeWriter.Create(recipient, identity, plaintext.Length);
        var ciphertext = new MemoryStream();
        await writer.EncryptToAsync(new MemoryStream(plaintext), ciphertext);
        return (writer.Envelope, ciphertext.ToArray());
    }

    private static async Task<byte[]> OpenAsync(
        SealedEnvelope envelope,
        SealingKeyPair recipient,
        SealedObjectIdentity identity,
        byte[] ciphertext,
        Random pieces)
    {
        var output = new MemoryStream();
        using var reader = SealedEnvelopeReader.Open(envelope, recipient, identity, output);
        var offset = 0;
        while (offset < ciphertext.Length)
        {
            var length = Math.Min(ciphertext.Length - offset, pieces.Next(1, 3 * ChunkSize));
            await reader.WriteAsync(ciphertext.AsMemory(offset, length));
            offset += length;
        }

        await reader.CompleteAsync();
        return output.ToArray();
    }

    private static SealedEnvelope SealDataKey(SealingPublicKey recipient, SealedEnvelopeHeader header, byte[] dataKey)
    {
        var headerBytes = header.Encode();
        var sealedDataKey = Hpke.Seal(recipient.PublicKey.Span, SealedEnvelopeFormat.HpkeInfo, headerBytes, dataKey, out var encapsulatedKey);
        return new SealedEnvelope(headerBytes, encapsulatedKey, sealedDataKey);
    }

    private static byte[] EncryptChunk(byte[] dataKey, long index, bool final, byte[] plaintext)
    {
        var nonce = new byte[SealedEnvelopeFormat.ChunkNonceSize];
        SealedEnvelopeFormat.ChunkNonce(index, final, nonce);
        var chunk = new byte[plaintext.Length + Tag];
        using var aes = new AesGcm(dataKey, Tag);
        aes.Encrypt(nonce, plaintext, chunk.AsSpan(0, plaintext.Length), chunk.AsSpan(plaintext.Length));
        return chunk;
    }
}

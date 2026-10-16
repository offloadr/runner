using Offloadr.Runner.Core.Sealing;
using Offloadr.Runner.V1;
using CoreSealingPublicKey = Offloadr.Runner.Core.Sealing.SealingPublicKey;
using ProtoSealedEnvelope = Offloadr.Runner.V1.SealedEnvelope;
using ProtoSealingPublicKey = Offloadr.Runner.V1.SealingPublicKey;

namespace Offloadr.Runner.Tests;

public class SessionTransferSealingTests
{
    [Test]
    public void Create_WithoutSealing_StillHasAReaderKeyButSealsNothing()
    {
        using var sealing = SessionTransferSealing.Create("session-1", requested: null, memoryWorkspace: false);

        Assert.Multiple(() =>
        {
            Assert.That(sealing.ProfileRequired, Is.False);
            Assert.That(sealing.SealsUploads, Is.False);
            Assert.That(sealing.ReaderKeyMessage.Algorithm, Is.EqualTo(SealedEnvelopeFormat.AlgorithmId));
            Assert.That(sealing.ReaderKeyMessage.PublicKey.Length, Is.EqualTo(65));
            Assert.That(sealing.ReaderKeyMessage.KeyId, Does.StartWith("reader-"));
            Assert.That(sealing.ToStatus(), Is.EqualTo(new SessionSealingStatus()));
        });
    }

    [Test]
    public void Create_EveryRuntimeGetsItsOwnReaderKey()
    {
        using var first = SessionTransferSealing.Create("session-1", null, memoryWorkspace: false);
        using var second = SessionTransferSealing.Create("session-1", null, memoryWorkspace: false);

        Assert.That(second.ReaderKeyMessage.KeyId, Is.Not.EqualTo(first.ReaderKeyMessage.KeyId));
        Assert.That(second.ReaderKeyMessage.PublicKey, Is.Not.EqualTo(first.ReaderKeyMessage.PublicKey));
    }

    [Test]
    public void Create_TheProfileWithARecipientSealsEverything()
    {
        using var recipient = SealingKeyPair.Generate("output-1");
        using var sealing = SessionTransferSealing.Create(
            "session-1",
            new SessionSealing { Required = true, OutputRecipient = ToMessage(recipient.PublicKey) },
            memoryWorkspace: true);

        Assert.That(sealing.ToStatus(), Is.EqualTo(new SessionSealingStatus
        {
            MemoryWorkspace = true,
            EncryptedUploads = true,
            EncryptedReadsRequired = true,
        }));
    }

    [Test]
    public void Create_RefusesTheProfileWithoutARecipientOrMemoryWorkspace()
    {
        using var recipient = SealingKeyPair.Generate("output-1");

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SessionTransferSealing.Create("session-1", new SessionSealing { Required = true }, memoryWorkspace: true),
                Throws.InvalidOperationException.With.Message.Contains("output recipient"));
            Assert.That(
                () => SessionTransferSealing.Create(
                    "session-1",
                    new SessionSealing { Required = true, OutputRecipient = ToMessage(recipient.PublicKey) },
                    memoryWorkspace: false),
                Throws.InvalidOperationException.With.Message.Contains("memory-backed"));
        });
    }

    [TestCase("unknown-algorithm", 65)]
    [TestCase(SealedEnvelopeFormat.AlgorithmId, 33)]
    public void Create_RefusesAnUnusableRecipient(string algorithm, int keyLength)
    {
        var key = new ProtoSealingPublicKey
        {
            KeyId = "output-1",
            Algorithm = algorithm,
            PublicKey = Google.Protobuf.ByteString.CopyFrom(new byte[keyLength]),
        };

        Assert.That(
            () => SessionTransferSealing.Create("session-1", new SessionSealing { OutputRecipient = key }, memoryWorkspace: false),
            Throws.InvalidOperationException.With.Message.Contains("output recipient key is unusable"));
    }

    [Test]
    public async Task ReadSink_OpensAPayloadSealedToTheReaderKey()
    {
        using var sealing = SessionTransferSealing.Create("session-1", null, memoryWorkspace: true);
        var identity = new SealedArtifactIdentity("session-1", "editor-1", "output", "", "a.png");
        var plaintext = Enumerable.Range(0, 70_000).Select(i => (byte)i).ToArray();
        var (envelope, ciphertext) = await SealAsync(ReaderPublicKey(sealing), identity, plaintext);
        using var output = new MemoryStream();

        using (var sink = SealedReadSink.Open(output, envelope, sealing, () => identity))
        {
            Assert.That(sink.IsSealed, Is.True);
            Assert.That(sink.SealedPlaintextSize, Is.EqualTo(plaintext.Length));
            Assert.That(sink.SealedCiphertextSize, Is.EqualTo(ciphertext.Length));
            // Transport chunks may cut the ciphertext anywhere.
            foreach (var piece in ciphertext.Chunk(1000))
            {
                await sink.WriteAsync(piece, CancellationToken.None);
            }

            await sink.CompleteAsync(CancellationToken.None);
        }

        Assert.That(output.ToArray(), Is.EqualTo(plaintext));
    }

    [Test]
    public async Task ReadSink_RefusesAnotherObjectATruncationAndPlaintextInTheProfile()
    {
        using var recipient = SealingKeyPair.Generate("output-1");
        using var profile = SessionTransferSealing.Create(
            "session-1",
            new SessionSealing { Required = true, OutputRecipient = ToMessage(recipient.PublicKey) },
            memoryWorkspace: true);
        var identity = new SealedArtifactIdentity("session-1", "editor-1", "output", "", "a.png");
        var other = identity with { Filename = "b.png" };
        var (envelope, ciphertext) = await SealAsync(ReaderPublicKey(profile), identity, [1, 2, 3]);

        Assert.Multiple(() =>
        {
            Assert.That(
                () => SealedReadSink.Open(new MemoryStream(), envelope: null, profile, () => identity),
                Throws.InvalidOperationException.With.Message.Contains("without an envelope"));
            Assert.That(
                () => SealedReadSink.Open(new MemoryStream(), envelope, profile, () => other),
                Throws.InvalidOperationException.With.Message.Contains("did not open"));
            Assert.That(
                () => SealedReadSink.Open(new MemoryStream(), envelope, sealing: null, () => identity),
                Throws.InvalidOperationException.With.Message.Contains("without a reader key"));
        });

        using var sink = SealedReadSink.Open(new MemoryStream(), envelope, profile, () => identity);
        await sink.WriteAsync(ciphertext.AsMemory(0, ciphertext.Length - 1), CancellationToken.None);
        Assert.That(
            async () => await sink.CompleteAsync(CancellationToken.None),
            Throws.InvalidOperationException.With.Message.Contains("incomplete"));
    }

    [Test]
    public void ReadSink_PassesPlaintextThroughOutsideTheProfile()
    {
        using var sealing = SessionTransferSealing.Create("session-1", null, memoryWorkspace: false);

        using var sink = SealedReadSink.Open(new MemoryStream(), envelope: null, sealing, () => throw new AssertionException("unused"));

        Assert.That(sink.IsSealed, Is.False);
    }

    [Test]
    public void WireLimit_LeavesRoomForTheTagOfEveryChunk()
    {
        const long fourGiB = 4L * 1024 * 1024 * 1024;

        Assert.Multiple(() =>
        {
            Assert.That(SealedReadSink.WireLimit(null, fourGiB), Is.EqualTo(fourGiB));
            Assert.That(SealedReadSink.WireLimit(new ProtoSealedEnvelope(), fourGiB), Is.EqualTo(fourGiB + (65536 * 16)));
            Assert.That(SealedReadSink.WireLimit(new ProtoSealedEnvelope(), 512L * 1024 * 1024), Is.EqualTo((512L * 1024 * 1024) + (8192 * 16)));
        });
    }

    [Test]
    public void CoreDumpHardening_LoadsTheShimWithNothingManaged()
    {
        var builder = new RunnerVfsEnvironmentBuilder();
        var env = new Dictionary<string, string?> { ["RUNNER_VFS_LIB"] = "/opt/shim.so", ["LD_PRELOAD"] = "/other.so" };

        var hardening = builder.BuildCoreDumpHardening(name => env.GetValueOrDefault(name), path => path == "/opt/shim.so");
        var missing = builder.BuildCoreDumpHardening(name => env.GetValueOrDefault(name), _ => false);

        Assert.That(hardening, Is.EquivalentTo(new Dictionary<string, string>
        {
            ["LD_PRELOAD"] = "/opt/shim.so:/other.so",
            ["VFS_ROOTS"] = string.Empty,
            ["VFS_NO_DUMP"] = "1",
        }));
        Assert.That(missing, Is.Empty);
    }

    [Test]
    public async Task LengthLimitedReadStream_StopsAtItsLength()
    {
        await using var limited = new LengthLimitedReadStream(new MemoryStream([1, 2, 3, 4, 5]), 3);
        using var copy = new MemoryStream();

        await limited.CopyToAsync(copy);

        Assert.That(copy.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
    }

    internal static ProtoSealingPublicKey ToMessage(CoreSealingPublicKey key)
        => new()
        {
            KeyId = key.KeyId,
            Algorithm = key.Algorithm,
            PublicKey = Google.Protobuf.ByteString.CopyFrom(key.PublicKey.Span),
        };

    internal static CoreSealingPublicKey ReaderPublicKey(SessionTransferSealing sealing)
        => CoreSealingPublicKey.Create(
            sealing.ReaderKeyMessage.KeyId,
            sealing.ReaderKeyMessage.Algorithm,
            sealing.ReaderKeyMessage.PublicKey.Span);

    /// <summary>Seals <paramref name="plaintext"/> as the control plane would for a read.</summary>
    internal static async Task<(ProtoSealedEnvelope Envelope, byte[] Ciphertext)> SealAsync(
        CoreSealingPublicKey recipient,
        SealedObjectIdentity identity,
        byte[] plaintext)
    {
        using var writer = SealedEnvelopeWriter.Create(recipient, identity, plaintext.Length);
        using var ciphertext = new MemoryStream();
        await writer.EncryptToAsync(new MemoryStream(plaintext), ciphertext);
        return (SessionTransferSealing.ToMessage(writer.Envelope), ciphertext.ToArray());
    }

    /// <summary>Opens an upload as the control plane would, with the output recipient's private key.</summary>
    internal static async Task<byte[]> OpenAsync(
        SealingKeyPair recipient,
        ProtoSealedEnvelope envelope,
        SealedObjectIdentity identity,
        byte[] ciphertext)
    {
        using var plaintext = new MemoryStream();
        using (var reader = SealedEnvelopeReader.Open(
                   new Offloadr.Runner.Core.Sealing.SealedEnvelope(envelope.Header.Memory, envelope.EncapsulatedKey.Memory, envelope.SealedDataKey.Memory),
                   recipient,
                   identity,
                   plaintext))
        {
            await reader.WriteAsync(ciphertext);
            await reader.CompleteAsync();
        }

        return plaintext.ToArray();
    }
}

using System.Buffers.Binary;
using Offloadr.Runner.Core.Sealing;

namespace Offloadr.Runner.Tests;

public class SealedEnvelopeHeaderTests
{
    // The worked example in docs/sealed-envelope.md.
    private const string ExampleHeader =
        "4f464c445345414c" // magic "OFLDSEAL"
        + "0001" // format version
        + "0010" + "0001" + "0001" // KEM, KDF, AEAD
        + "0001" // payload: AES-256-GCM STREAM
        + "00010000" // chunk size 65536
        + "0000000000010001" // plaintext size 65537
        + "0005" + "72656164" + "31" // key id "read1"
        + "01" // identity: artifact
        + "0002" + "7331" // session id "s1"
        + "0002" + "6531" // editor sid "e1"
        + "0006" + "6f7574707574" // type "output"
        + "0000" // subfolder ""
        + "0005" + "612e706e67"; // filename "a.png"

    [Test]
    public void Encode_ProducesTheDocumentedLayout()
    {
        var header = new SealedEnvelopeHeader("read1", 65537, new SealedArtifactIdentity("s1", "e1", "output", "", "a.png"));

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexStringLower(header.Encode()), Is.EqualTo(ExampleHeader));
            Assert.That(SealedEnvelopeHeader.Parse(Convert.FromHexString(ExampleHeader)), Is.EqualTo(header));
        });
    }

    [Test]
    public void EncodeAndParse_RoundTripEveryIdentityKind()
    {
        var identities = new SealedObjectIdentity[]
        {
            new SealedArtifactIdentity("session", "editor", "input", "a/b", "ä ö.png"),
            new SealedWorkspaceFileIdentity("session", "custom_nodes", "pack/nodes/__init__.py"),
            new SealedSeedArchiveIdentity("session", "user"),
        };

        Assert.Multiple(() =>
        {
            foreach (var identity in identities)
            {
                var header = new SealedEnvelopeHeader("key", 12345, identity);
                Assert.That(SealedEnvelopeHeader.Parse(header.Encode()), Is.EqualTo(header));
            }
        });
    }

    [Test]
    public void Encode_EnforcesFieldLimits()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                () => new SealedEnvelopeHeader("key", 1, new SealedArtifactIdentity("s", "e", "t", "", new string('x', 256))).Encode(),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("Filename"));
            Assert.That(
                new SealedEnvelopeHeader("key", 1, new SealedArtifactIdentity("s", "e", "t", "", new string('x', 255))).Encode(),
                Is.Not.Empty);
            Assert.That(
                () => new SealedEnvelopeHeader("key", 1, new SealedArtifactIdentity("s", "e", "t", "", new string('ä', 128))).Encode(),
                Throws.InstanceOf<SealedEnvelopeException>(),
                "limits count UTF-8 bytes, not characters");
            Assert.That(
                () => new SealedEnvelopeHeader("key", 1, new SealedWorkspaceFileIdentity("s", "user", new string('x', 4097))).Encode(),
                Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(
                () => new SealedEnvelopeHeader(new string('k', 129), 1, new SealedSeedArchiveIdentity("s", "user")).Encode(),
                Throws.InstanceOf<SealedEnvelopeException>());
            Assert.That(
                () => new SealedEnvelopeHeader("key", 1, new SealedSeedArchiveIdentity("", "user")).Encode(),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("empty"));
            Assert.That(
                () => new SealedEnvelopeHeader("key", 1, new SealedSeedArchiveIdentity("s", "\ud800")).Encode(),
                Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("Unicode"));
        });
    }

    [Test]
    public void Parse_RejectsNonCanonicalOrUnsupportedHeaders()
    {
        var valid = Convert.FromHexString(ExampleHeader);

        Assert.Multiple(() =>
        {
            AssertRejected([.. valid, 0x00], "trailing");
            AssertRejected(valid[..^1], "truncated");
            AssertRejected(Patched(valid, 0, 0x6F), "Not a sealed envelope");
            AssertRejected(Patched(valid, 9, 0x02), "version");
            AssertRejected(Patched(valid, 11, 0x20), "suite");
            AssertRejected(Patched(valid, 13, 0x03), "suite");
            AssertRejected(Patched(valid, 15, 0x02), "suite");
            AssertRejected(Patched(valid, 17, 0x02), "suite");
            AssertRejected(Patched(valid, 20, 0x02), "chunk size");
            AssertRejected(WithPlaintextSize(valid, SealedEnvelopeFormat.MaxPlaintextSize + 1), "larger than");
            AssertRejected(WithPlaintextSize(valid, ulong.MaxValue), "larger than");
            AssertRejected(Patched(valid, 37, 0x04), "identity kind");
            AssertRejected(Patched(valid, 41, 0xFF), "UTF-8");
            AssertRejected(new byte[SealedEnvelopeHeader.MaxEncodedSize + 1], "too large");
            Assert.That(SealedEnvelopeHeader.Parse(WithPlaintextSize(valid, SealedEnvelopeFormat.MaxPlaintextSize)).PlaintextSize,
                Is.EqualTo(SealedEnvelopeFormat.MaxPlaintextSize));
        });
    }

    [Test]
    public void Parse_RejectsAStringLongerThanItsField()
    {
        var header = new SealedEnvelopeHeader("key", 1, new SealedArtifactIdentity("s", "e", "t", "", "f"));
        var encoded = header.Encode();
        // Grow the 1-byte filename to 256 bytes, one over its limit.
        var tooLong = encoded[..^3].Concat(new byte[] { 0x01, 0x00 }).Concat(Enumerable.Repeat((byte)'x', 256)).ToArray();

        Assert.That(
            () => SealedEnvelopeHeader.Parse(tooLong),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("longer than 255"));
    }

    [TestCase(0L, false, "000000000000000000000000")]
    [TestCase(0L, true, "000000000000000000000001")]
    [TestCase(1L, false, "000000000000000000000100")]
    [TestCase(65535L, true, "000000000000000000ffff01")]
    public void ChunkNonce_IsAnElevenByteBigEndianCounterAndAFinalFlag(long counter, bool final, string expected)
    {
        var nonce = new byte[SealedEnvelopeFormat.ChunkNonceSize];

        SealedEnvelopeFormat.ChunkNonce(counter, final, nonce);

        Assert.That(Convert.ToHexStringLower(nonce), Is.EqualTo(expected));
    }

    [Test]
    public void ChunkNonce_RejectsCounterOverflow()
    {
        var nonce = new byte[SealedEnvelopeFormat.ChunkNonceSize];

        Assert.That(
            () => SealedEnvelopeFormat.ChunkNonce(SealedEnvelopeFormat.MaxChunkCount, final: false, nonce),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains("overflow"));
    }

    private static void AssertRejected(byte[] encoded, string message)
        => Assert.That(
            () => SealedEnvelopeHeader.Parse(encoded),
            Throws.InstanceOf<SealedEnvelopeException>().With.Message.Contains(message));

    private static byte[] Patched(byte[] encoded, int offset, byte value)
    {
        var copy = (byte[])encoded.Clone();
        copy[offset] = value;
        return copy;
    }

    private static byte[] WithPlaintextSize(byte[] encoded, ulong size)
    {
        var copy = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt64BigEndian(copy.AsSpan(22), size);
        return copy;
    }

    private static byte[] WithPlaintextSize(byte[] encoded, long size) => WithPlaintextSize(encoded, (ulong)size);
}

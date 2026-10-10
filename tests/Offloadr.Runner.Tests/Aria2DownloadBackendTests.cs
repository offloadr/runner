using System.Buffers.Binary;

namespace Offloadr.Runner.Tests;

public class Aria2DownloadBackendTests
{
    [TestCase("complete", true)]
    [TestCase("ERROR", true)]
    [TestCase("removed", true)]
    [TestCase("active", false)]
    [TestCase("paused", false)]
    [TestCase(null, false)]
    public void IsStoppedStatus_ClassifiesAria2ResultStates(string? status, bool expected)
    {
        Assert.That(Aria2DownloadBackend.IsStoppedStatus(status), Is.EqualTo(expected));
    }

    [TestCase(1L, "GID 0123456789abcdef is not found", true)]
    [TestCase(1L, "GID abc is not found", true)]
    [TestCase(1L, "GID ABCDEF is not found", true)]
    [TestCase(1L, " GID 0123456789abcdef is not found ", true)]
    [TestCase(1L, "Invalid GID not-hex", true)]
    [TestCase(1L, "GID 0123456789abcdef is not unique", false)]
    [TestCase(1L, "GID#0123456789abcdef cannot be removed now", false)]
    [TestCase(1L, "Internal JSON-RPC failure", false)]
    [TestCase(-32603L, "GID 0123456789abcdef is not found", false)]
    [TestCase(1L, "Invalid GID ", false)]
    [TestCase(1L, null, false)]
    public void IsUnavailableGidError_ClassifiesOnlyVerifiedMissingOrInvalidGidResponses(
        long resultCode,
        string? resultMessage,
        bool expected)
    {
        Assert.That(
            Aria2DownloadBackend.IsUnavailableGidError(resultCode, resultMessage),
            Is.EqualTo(expected));
    }

    [Test]
    public async Task BuildOptions_UsesManagedResumeAndReadinessContract()
    {
        var settings = Aria2Settings.FromEnvironment(name => name switch
        {
            "ARIA2_MAX_CONNECTION_PER_SERVER" => "12",
            "ARIA2_SPLIT" => "10",
            "ARIA2_MIN_SPLIT_SIZE" => "6M",
            "ARIA2_FILE_ALLOCATION" => "trunc",
            _ => null
        }, () => "secret");
        await using var backend = new Aria2DownloadBackend(settings);
        var request = new ModelTransferCreateRequest(
            "/models/model.safetensors",
            1024,
            ["https://models.example/model"],
            MetalinkContent: null,
            PreferredIdentifier: "0123456789abcdef",
            StartPaused: true,
            QueuePosition: null,
            ResumeMode: ModelTransferResumeMode.InitializeEmpty);

        var options = backend.BuildOptions(request);

        Assert.Multiple(() =>
        {
            Assert.That(options["allow-overwrite"], Is.EqualTo("true"));
            Assert.That(options["auto-file-renaming"], Is.EqualTo("false"));
            Assert.That(options["always-resume"], Is.EqualTo("true"));
            Assert.That(options["continue"], Is.EqualTo("false"));
            Assert.That(options["disk-cache"], Is.EqualTo("0"));
            Assert.That(options["stream-piece-selector"], Is.EqualTo("inorder"));
            Assert.That(options["piece-length"], Is.EqualTo("1M"));
            Assert.That(options["pause"], Is.EqualTo("true"));
            Assert.That(options["gid"], Is.EqualTo("0123456789abcdef"));
            Assert.That(options["max-connection-per-server"], Is.EqualTo("12"));
            Assert.That(options["split"], Is.EqualTo("10"));
            Assert.That(options["min-split-size"], Is.EqualTo("6M"));
            Assert.That(options["file-allocation"], Is.EqualTo("trunc"));
        });
    }

    [Test]
    public async Task BuildOptions_FullTransferKeepsSequentialContinuation()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
        await using var backend = new Aria2DownloadBackend(settings);
        var request = new ModelTransferCreateRequest(
            "/models/model.bin",
            1024,
            ["https://models.example/model"],
            MetalinkContent: null,
            PreferredIdentifier: null,
            StartPaused: false,
            QueuePosition: null);

        var options = backend.BuildOptions(request);

        Assert.That(options["continue"], Is.EqualTo("true"));
    }

    [Test]
    public void ShouldUseMetalink_DeterministicRequestWithMetalink_UsesSourceUris()
    {
        var request = new ModelTransferCreateRequest(
            "/models/model.bin",
            1024,
            ["https://models.example/model"],
            MetalinkContent: "<metalink />"u8.ToArray(),
            PreferredIdentifier: "0123456789abcdef",
            StartPaused: true,
            QueuePosition: null,
            ResumeMode: ModelTransferResumeMode.InitializeEmpty);

        Assert.That(Aria2DownloadBackend.ShouldUseMetalink(request), Is.False);
    }

    [Test]
    public void ShouldUseMetalink_ConventionalRequestWithMetalink_UsesMetalink()
    {
        var request = new ModelTransferCreateRequest(
            "/models/model.bin",
            1024,
            ["https://models.example/model"],
            MetalinkContent: "<metalink />"u8.ToArray(),
            PreferredIdentifier: null,
            StartPaused: false,
            QueuePosition: null);

        Assert.That(Aria2DownloadBackend.ShouldUseMetalink(request), Is.True);
    }

    [Test]
    public void PrepareResumeState_SeedsEmptyAria2ControlFileWithoutChangingPlaceholderLength()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aria2-control-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            const long expectedLength = (2L * 1024 * 1024) + 17;
            var destination = Path.Combine(root, "model.bin");
            using (var stream = File.Create(destination))
            {
                stream.SetLength(expectedLength);
            }

            var request = new ModelTransferCreateRequest(
                destination,
                expectedLength,
                ["https://models.example/model"],
                MetalinkContent: null,
                PreferredIdentifier: "0123456789abcdef",
                StartPaused: true,
                QueuePosition: null,
                ResumeMode: ModelTransferResumeMode.InitializeEmpty);

            Aria2DownloadBackend.PrepareResumeState(request);
            Aria2DownloadBackend.PrepareResumeState(request);

            var contents = File.ReadAllBytes($"{destination}.aria2");
            Assert.Multiple(() =>
            {
                Assert.That(new FileInfo(destination).Length, Is.EqualTo(expectedLength));
                Assert.That(BinaryPrimitives.ReadUInt16BigEndian(contents), Is.EqualTo(1));
                Assert.That(BinaryPrimitives.ReadUInt32BigEndian(contents.AsSpan(2)), Is.Zero);
                Assert.That(BinaryPrimitives.ReadUInt32BigEndian(contents.AsSpan(6)), Is.Zero);
                Assert.That(BinaryPrimitives.ReadUInt32BigEndian(contents.AsSpan(10)), Is.EqualTo(1024 * 1024));
                Assert.That(BinaryPrimitives.ReadInt64BigEndian(contents.AsSpan(14)), Is.EqualTo(expectedLength));
                Assert.That(BinaryPrimitives.ReadInt64BigEndian(contents.AsSpan(22)), Is.Zero);
                Assert.That(BinaryPrimitives.ReadInt32BigEndian(contents.AsSpan(30)), Is.EqualTo(1));
                Assert.That(contents[34], Is.Zero);
                Assert.That(BinaryPrimitives.ReadUInt32BigEndian(contents.AsSpan(35)), Is.Zero);
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void PrepareResumeState_RejectsUnverifiedExistingControlFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aria2-control-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "model.bin");
            File.WriteAllBytes(destination, new byte[1024]);
            File.WriteAllBytes($"{destination}.aria2", [1, 2, 3]);
            var request = new ModelTransferCreateRequest(
                destination,
                1024,
                ["https://models.example/model"],
                MetalinkContent: null,
                PreferredIdentifier: "0123456789abcdef",
                StartPaused: true,
                QueuePosition: null,
                ResumeMode: ModelTransferResumeMode.InitializeEmpty);

            Assert.That(
                () => Aria2DownloadBackend.PrepareResumeState(request),
                Throws.InvalidOperationException.With.Message.Contains("unverified existing resume state"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void PrepareResumeState_RequiresPreviouslyVerifiedControlFileForReattachment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"aria2-control-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "model.bin");
            File.WriteAllBytes(destination, new byte[1024]);
            var request = new ModelTransferCreateRequest(
                destination,
                1024,
                ["https://models.example/model"],
                MetalinkContent: null,
                PreferredIdentifier: "0123456789abcdef",
                StartPaused: true,
                QueuePosition: null,
                ResumeMode: ModelTransferResumeMode.RequireExisting);

            Assert.That(
                () => Aria2DownloadBackend.PrepareResumeState(request),
                Throws.InvalidOperationException.With.Message.Contains("missing"));

            File.WriteAllBytes($"{destination}.aria2", [1, 2, 3]);
            Assert.That(() => Aria2DownloadBackend.PrepareResumeState(request), Throws.Nothing);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task BuildOptions_RejectsNonCanonicalPreferredIdentifier()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
        await using var backend = new Aria2DownloadBackend(settings);
        var request = new ModelTransferCreateRequest(
            "/models/model.bin",
            1024,
            ["https://models.example/model"],
            MetalinkContent: null,
            PreferredIdentifier: "ABCDEF0123456789",
            StartPaused: true,
            QueuePosition: null);

        Assert.That(
            () => backend.BuildOptions(request),
            Throws.InvalidOperationException.With.Message.Contains("16 lowercase hexadecimal"));
    }
}

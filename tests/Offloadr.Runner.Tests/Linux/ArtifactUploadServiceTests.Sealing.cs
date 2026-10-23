using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Core.Sealing;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [Test]
    public async Task FolderMovedIntoTheOutput_HasItsFilesUploaded()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: false);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            // Written elsewhere, then moved in whole: no event names the file inside.
            var staged = Path.Combine(root, "staged-batch");
            Directory.CreateDirectory(staged);
            await File.WriteAllBytesAsync(Path.Combine(staged, "x.png"), [1, 2, 3]);
            Directory.Move(staged, Path.Combine(paths.OutputDirectory, "batch"));

            var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(10));
            Assert.That(upload.Metadata!.Subfolder, Is.EqualTo("batch"));
            Assert.That(upload.Metadata.Filename, Is.EqualTo("x.png"));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SealedSession_UploadsCiphertextOnlyTheOutputRecipientOpens()
    {
        var root = CreateTempDirectory();
        using var recipient = SealingKeyPair.Generate("output-1");
        using var sealing = SessionTransferSealing.Create(
            "session-1",
            new SessionSealing { Required = true, OutputRecipient = SessionTransferSealingTests.ToMessage(recipient.PublicKey) },
            memoryWorkspace: true);
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true) with { TransferSealing = sealing };
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            var content = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 7)).ToArray();
            Directory.CreateDirectory(Path.Combine(paths.OutputDirectory, "sub"));
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "sub", "x.png"), content);
            var upload = await artifactClient.WaitForUploadAsync(TimeSpan.FromSeconds(5));

            Assert.Multiple(() =>
            {
                Assert.That(upload.Metadata!.Envelope, Is.Not.Null);
                Assert.That(upload.Metadata.SizeBytes, Is.EqualTo(content.Length), "the catalog keeps the plaintext size");
                Assert.That(upload.Metadata.Sha256, Is.Null);
                Assert.That(upload.Payload.Length, Is.EqualTo(SealedEnvelopeFormat.CiphertextSize(content.Length)));
                Assert.That(upload.Payload.AsSpan().IndexOf(content.AsSpan(0, 64)), Is.EqualTo(-1));
            });
            var opened = await SessionTransferSealingTests.OpenAsync(
                recipient,
                upload.Metadata!.Envelope,
                new SealedArtifactIdentity("session-1", "editor-1", "output", "sub", "x.png"),
                upload.Payload);
            Assert.That(opened, Is.EqualTo(content));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SealedUpload_ThatFailsMidwayDisposesItsCall()
    {
        var root = CreateTempDirectory();
        using var recipient = SealingKeyPair.Generate("output-1");
        using var sealing = SessionTransferSealing.Create(
            "session-1",
            new SessionSealing { OutputRecipient = SessionTransferSealingTests.ToMessage(recipient.PublicKey) },
            memoryWorkspace: true);
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true) with { TransferSealing = sealing };
            var outputPath = Path.Combine(paths.OutputDirectory, "shrinking.png");
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse());
            // The editor truncates the output once the upload has started: sealing then runs short.
            artifactClient.AfterUploadMetadata = () => File.WriteAllBytes(outputPath, [1]);
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);

            await File.WriteAllBytesAsync(outputPath, new byte[100_000]);

            await artifactClient.FirstUploadCallDisposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(artifactClient.FirstUploadCallCompleted.Task.IsCompleted, Is.False,
                "the truncated first upload failed before completing its request");
            // The watcher can immediately retry the changed file. Quiesce that
            // worker before comparing the counters for all of its calls.
            await service.StopSessionAsync("session-1");
            Assert.That(artifactClient.UploadCallsStarted, Is.GreaterThan(0));
            Assert.That(artifactClient.UploadCallsDisposed, Is.EqualTo(artifactClient.UploadCallsStarted), "no upload call is left open");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SealedSession_ReadbackOpensWithTheReaderKeyAndIsNotUploadedAgain()
    {
        var root = CreateTempDirectory();
        using var sealing = SessionTransferSealing.Create("session-1", null, memoryWorkspace: true);
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true) with { TransferSealing = sealing };
            var content = "earlier output"u8.ToArray();
            var (envelope, ciphertext) = await SessionTransferSealingTests.SealAsync(
                SessionTransferSealingTests.ReaderPublicKey(sealing),
                new SealedArtifactIdentity("session-1", "editor-1", "output", "", "old.png"),
                content);
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = ciphertext,
                ReadArtifactEnvelope = envelope,
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "old.png");

            Assert.That(await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None), Is.True);

            Assert.That(await File.ReadAllBytesAsync(outputPath), Is.EqualTo(content));
            Assert.That(artifactClient.LastReadArtifactRequest!.ReaderKey, Is.EqualTo(sealing.ReaderKeyMessage));
            await Task.Delay(500);
            Assert.That(artifactClient.UploadCount, Is.Zero, "a downloaded artifact is not uploaded back");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task SealedProfile_RefusesAPlaintextReadbackAndPublishesNothing()
    {
        var root = CreateTempDirectory();
        using var recipient = SealingKeyPair.Generate("output-1");
        using var sealing = SessionTransferSealing.Create(
            "session-1",
            new SessionSealing { Required = true, OutputRecipient = SessionTransferSealingTests.ToMessage(recipient.PublicKey) },
            memoryWorkspace: true);
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true) with { TransferSealing = sealing };
            var artifactClient = new FakeRunnerArtifactClient(new RunnerArtifactServiceListArtifactsResponse())
            {
                ReadArtifactContent = "plaintext from an old peer"u8.ToArray(),
            };
            await using var service = new ArtifactUploadService("runner-secret-value", artifactClient, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            var outputPath = Path.Combine(paths.OutputDirectory, "old.png");

            bool available;
            try
            {
                available = await service.TryEnsureArtifactAvailableAsync(outputPath, highPriority: true, CancellationToken.None);
            }
            catch (InvalidOperationException ex)
            {
                Assert.That(ex.Message, Does.Contain("without an envelope"));
                available = false;
            }

            Assert.That(available, Is.False);
            Assert.That(File.Exists(outputPath) && new FileInfo(outputPath).Length > 0, Is.False);
            Assert.That(Directory.GetFiles(paths.OutputDirectory, "*.part", SearchOption.AllDirectories), Is.Empty);
        }
        finally
        {
            TryDelete(root);
        }
    }
}

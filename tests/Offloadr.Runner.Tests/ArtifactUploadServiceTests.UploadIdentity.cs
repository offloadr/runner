using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task UploadRetryUsesTheSameIdentityOnlyForAnUnchangedSnapshot(bool changeFile)
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "reply-loss.txt");
            var client = new FakeRunnerArtifactClient(new());
            var replies = 0;
            client.UploadResponseFailure = _ =>
            {
                if (Interlocked.Increment(ref replies) != 1) return null;
                if (changeFile) File.WriteAllBytes(path, [9, 8, 7, 6, 5]);
                return new RpcException(new Status(StatusCode.Internal, "First committed response lost"));
            };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);

            var first = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            var retry = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(Guid.TryParseExact(first.Metadata!.UploadId, "N", out _), Is.True);
            Assert.That(retry.Metadata!.UploadId,
                changeFile ? Is.Not.EqualTo(first.Metadata.UploadId) : Is.EqualTo(first.Metadata.UploadId));
            Assert.That(retry.ChunkBytes, Is.EqualTo(changeFile ? 5 : 3));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task RescanAfterExhaustedRepliesRetainsTheOriginalSnapshotIdentity()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new FakeRunnerArtifactClient(new())
            {
                UploadResponseFailure = _ => new RpcException(new Status(StatusCode.Internal, "Response lost"))
            };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "rescan.txt"), [1, 2, 3]);

            var ids = new List<string>();
            for (var i = 0; i < 4; i++)
                ids.Add((await client.WaitForUploadAsync(TimeSpan.FromSeconds(5))).Metadata!.UploadId);
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(ids.Distinct().Count(), Is.EqualTo(1));

            client.UploadResponseFailure = null;
            await service.RefreshSessionAsync("session-1");
            var rescanned = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(rescanned.Metadata!.UploadId, Is.EqualTo(ids[0]));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task WrongCommittedUploadIdentityDoesNotMarkTheFileSaved()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new FakeRunnerArtifactClient(new()) { ReturnWrongUploadId = true };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "wrong-receipt.txt"), [1, 2, 3]);
            for (var i = 0; i < 4; i++)
                await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [TestCase("filename", true, 0)]
    [TestCase("subfolder", true, 0)]
    [TestCase("type", true, 0)]
    [TestCase("filename", false, 1)]
    [TestCase("subfolder", false, 1)]
    [TestCase("type", false, 1)]
    public async Task CommittedReceiptMatchesCatalogPathIdentity(string field, bool caseOnly, int expectedUnsaved)
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var directory = Path.Combine(paths.OutputDirectory, "nested");
            Directory.CreateDirectory(directory);
            var client = new FakeRunnerArtifactClient(new())
            {
                RewriteUploadArtifact = artifact =>
                {
                    string Rewrite(string value) => caseOnly ? value.ToUpperInvariant() : value + "-different";
                    switch (field)
                    {
                        case "filename": artifact.Filename = Rewrite(artifact.Filename); break;
                        case "subfolder": artifact.Subfolder = Rewrite(artifact.Subfolder); break;
                        case "type": artifact.Type = Rewrite(artifact.Type); break;
                    }
                    return artifact;
                }
            };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(directory, "receipt.txt"), [1, 2, 3]);
            var captured = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(Guid.TryParseExact(captured.Metadata!.UploadId, "N", out _), Is.True);
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(expectedUnsaved));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task WaitForIdentityUploadsToFinishAsync(ArtifactUploadService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.GetPendingUploadCount("session-1") != 0)
            await Task.Delay(10, timeout.Token);
    }

    private static SessionProcessManager.SessionPaths UploadIdentityPaths(string root)
    {
        var paths = new SessionProcessManager.SessionPaths
        {
            HomeDirectory = root,
            UserDirectory = root,
            InputDirectory = Path.Combine(root, "input"),
            OutputDirectory = Path.Combine(root, "output"),
            TempDirectory = Path.Combine(root, "temp"),
            CacheDirectory = root,
            LogsDirectory = root
        };
        Directory.CreateDirectory(paths.InputDirectory);
        Directory.CreateDirectory(paths.OutputDirectory);
        Directory.CreateDirectory(paths.TempDirectory);
        return paths;
    }
}

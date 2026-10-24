namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingUncertainSnapshotReleasesWatcherCapacityWithoutAnExplicitRefresh(bool rename)
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var originalPath = Path.Combine(paths.OutputDirectory, "uncertain.bin");
            var client = new FakeRunnerArtifactClient(new()) { RejectAsRetiredUpload = true };
            var logger = new ListLogger<ArtifactUploadService>();
            var policy = new ArtifactUploadPolicy { MaximumUnacknowledgedSnapshots = 1 };
            await using var service = new ArtifactUploadService("secret", client, logger, uploadPolicy: policy);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(originalPath, [1, 2, 3]);
            var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "waiting.bin"), [4, 5, 6]);
            await WaitForUploadLogAsync(logger, "Artifact upload queue is full");
            client.RejectAsRetiredUpload = false;

            if (rename) File.Move(originalPath, Path.Combine(paths.OutputDirectory, "renamed.bin"));
            else File.Delete(originalPath);

            var expected = rename ? new[] { "waiting.bin", "renamed.bin" } : ["waiting.bin"];
            var uploaded = new List<string>();
            foreach (var _ in expected)
            {
                var next = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                uploaded.Add(next.Metadata!.Filename);
                Assert.That(next.Metadata.UploadId, Is.Not.EqualTo(original.Metadata!.UploadId));
            }
            Assert.That(uploaded, Is.EquivalentTo(expected));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(1 + expected.Length));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task AutomaticRescanKeepsElapsedExhaustionUntilAChangedSnapshotOrExplicitRefresh(bool refresh)
    {
        var root = CreateTempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "expired.bin");
            var client = new FakeRunnerArtifactClient(new()) { UploadGate = release.Task };
            var logger = new ListLogger<ArtifactUploadService>();
            var clock = new ArtifactUploadBudgetTests.UploadClock();
            var policy = new ArtifactUploadPolicy
            {
                Clock = clock,
                MaximumUnacknowledgedSnapshots = 1,
                MetadataTimeout = TimeSpan.FromSeconds(2),
                NoProgressTimeout = TimeSpan.FromSeconds(2),
                ReceiptTimeout = TimeSpan.FromSeconds(2)
            };
            await using var service = new ArtifactUploadService("secret", client, logger, uploadPolicy: policy);
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                await client.LastPreparedUploadResponse!.WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "waiting.bin"), [4, 5, 6]);
                await WaitForUploadLogAsync(logger, "Artifact upload queue is full");
                var completedScans = logger.Snapshot().Count(entry => entry.Message.StartsWith("Artifact scan completed", StringComparison.Ordinal));
                clock.Advance(TimeSpan.FromSeconds(6));
                await WaitForUploadLogAsync(logger, "Rescanning artifacts after the upload queue drained");
                using (var scanned = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (logger.Snapshot().Count(entry => entry.Message.StartsWith("Artifact scan completed", StringComparison.Ordinal)) < completedScans + 2)
                        await Task.Delay(10, scanned.Token);
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.UploadCount, Is.EqualTo(1), "An automatic scan cannot renew the original snapshot's elapsed allowance.");
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(1));
                Assert.That(client.UploadCallsCancelled, Is.EqualTo(1));

                client.UploadGate = null;
                if (refresh) await service.RefreshSessionAsync("session-1");
                else await File.WriteAllBytesAsync(path, [9, 8, 7, 6, 5]);
                var recovered = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(recovered.Metadata!.Filename, Is.EqualTo("expired.bin"));
                Assert.That(recovered.Metadata.UploadId,
                    refresh ? Is.EqualTo(original.Metadata!.UploadId) : Is.Not.EqualTo(original.Metadata!.UploadId));
                var waiting = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(waiting.Metadata!.Filename, Is.EqualTo("waiting.bin"));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { release.TrySetResult(); }
        }
        finally { TryDelete(root); }
    }
}

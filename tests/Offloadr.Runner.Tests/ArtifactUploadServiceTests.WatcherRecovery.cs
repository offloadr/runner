using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [TestCase("output", false)]
    [TestCase("output", true)]
    [TestCase("temp", false)]
    [TestCase("temp", true)]
    public async Task ReplacingAnArtifactRootReclaimsItsUncertainBindings(string type, bool rename)
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var directory = type == "output" ? paths.OutputDirectory : paths.TempDirectory;
            var other = type == "output" ? paths.TempDirectory : paths.OutputDirectory;
            var client = new FakeRunnerArtifactClient(new()) { RejectAsRetiredUpload = true };
            var logger = new ListLogger<ArtifactUploadService>();
            await using var service = new ArtifactUploadService("secret", client, logger,
                uploadPolicy: new ArtifactUploadPolicy { MaximumUnacknowledgedSnapshots = 1 });
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(directory, "uncertain.bin"), [1, 2, 3]);
            var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await File.WriteAllBytesAsync(Path.Combine(other, "waiting.bin"), [4, 5, 6]);
            await WaitForUploadLogAsync(logger, "Artifact upload queue is full");
            client.RejectAsRetiredUpload = false;

            if (rename) Directory.Move(directory, Path.Combine(root, "retired-" + type));
            else Directory.Delete(directory, recursive: true);
            await WaitForUploadLogAsync(logger, "Artifact watcher removed");
            Directory.CreateDirectory(directory);
            await File.WriteAllBytesAsync(Path.Combine(directory, "fresh.bin"), [7, 8, 9]);

            var names = new List<string>();
            for (var i = 0; i < 2; i++)
            {
                var uploaded = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                names.Add(uploaded.Metadata!.Filename);
                Assert.That(uploaded.Metadata.UploadId, Is.Not.EqualTo(original.Metadata!.UploadId));
            }
            Assert.That(names, Is.EquivalentTo(new[] { "waiting.bin", "fresh.bin" }));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(3));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test, NonParallelizable]
    public async Task RecognizedCatalogFilesReleaseTheirQueuedUploadBudgets()
    {
        var root = CreateTempDirectory();
        var previousLimit = ArtifactUploadService.MaxPendingUploads;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ArtifactUploadService.MaxPendingUploads = 2;
            var paths = UploadIdentityPaths(root);
            var listing = new RunnerArtifactServiceListArtifactsResponse();
            var client = new FakeRunnerArtifactClient(listing);
            var logger = new ListLogger<ArtifactUploadService>();
            await using var service = new ArtifactUploadService("secret", client, logger,
                uploadPolicy: new ArtifactUploadPolicy { MaximumUnacknowledgedSnapshots = 2 });
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                for (var i = 0; i < 4; i++)
                {
                    release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    client.UploadGate = release.Task;
                    var blocker = $"blocker-{i}.bin";
                    await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, blocker), [1, 2, 3]);
                    var blocked = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                    Assert.That(blocked.Metadata!.Filename, Is.EqualTo(blocker));
                    await client.LastPreparedUploadResponse!.WaitAsync(TimeSpan.FromSeconds(5));

                    // Catalog recognition lands after queue admission but before the
                    // single worker opens this file, as with a just-published readback.
                    var filename = $"catalog-{i}.bin";
                    byte[] bytes = [4, 5, 6];
                    await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, filename), bytes);
                    using (var queued = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                        while (service.GetPendingUploadCount("session-1") != 2) await Task.Delay(10, queued.Token);
                    listing.Artifacts.Add(new EditorArtifactMetadata
                    {
                        EditorSid = "editor-1",
                        Type = "output",
                        Filename = filename,
                        SizeBytes = bytes.Length,
                        Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))
                    });
                    await service.RefreshSessionAsync("session-1");
                    release.TrySetResult();
                    await WaitForIdentityUploadsToFinishAsync(service);
                    Assert.That(client.IssuedIdentityCount, Is.EqualTo(i + 1), "A recognized catalog version needs no upload identity.");
                }

                client.UploadGate = null;
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "new-output.bin"), [7, 8, 9]);
                var next = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(next.Metadata!.Filename, Is.EqualTo("new-output.bin"));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(5));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { release.TrySetResult(); }
        }
        finally
        {
            ArtifactUploadService.MaxPendingUploads = previousLimit;
            TryDelete(root);
        }
    }

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

    [Test, NonParallelizable]
    public async Task ExhaustionBeforeIdentityIssuanceRemainsBoundedAndMissingFilesReleaseItsCapacity()
    {
        var root = CreateTempDirectory();
        var previousLimit = ArtifactUploadService.MaxPendingUploads;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ArtifactUploadService.MaxPendingUploads = 1;
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "first.bin");
            var client = new FakeRunnerArtifactClient(new()) { IssueIdentityGate = release.Task };
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
                await client.IssueIdentityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "waiting.bin"), [4, 5, 6]);
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "excess.bin"), [7, 8, 9]);
                await WaitForUploadLogAsync(logger, "Artifact upload queue is full");
                clock.Advance(TimeSpan.FromSeconds(6));
                using (var admitted = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (client.IssuedIdentityCount < 2) await Task.Delay(10, admitted.Token);
                var completedScans = logger.Snapshot().Count(entry => entry.Message.StartsWith("Artifact scan completed", StringComparison.Ordinal));
                clock.Advance(TimeSpan.FromSeconds(6));
                using (var scanned = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    while (logger.Snapshot().Count(entry => entry.Message.StartsWith("Artifact scan completed", StringComparison.Ordinal)) < completedScans + 2)
                        await Task.Delay(10, scanned.Token);
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2), "Two retained budgets fill the pending plus uncertainty allowance.");
                Assert.That(client.UploadCount, Is.Zero, "No issuance response or upload has completed.");

                release.TrySetResult();
                File.Delete(path);
                var recovered = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(recovered.Metadata!.Filename, Is.AnyOf("waiting.bin", "excess.bin"));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(3));
                Assert.That(client.UploadCount, Is.EqualTo(1));
                Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
            }
            finally { release.TrySetResult(); }
        }
        finally
        {
            ArtifactUploadService.MaxPendingUploads = previousLimit;
            TryDelete(root);
        }
    }
}

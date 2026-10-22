using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.EditorRuntime.V1;
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
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(1));

            client.UploadResponseFailure = null;
            await service.RefreshSessionAsync("session-1");
            var rescanned = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(rescanned.Metadata!.UploadId, Is.EqualTo(ids[0]));
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(1));
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

    [TestCase(false, false, 1)]
    [TestCase(false, true, 0)]
    [TestCase(true, false, 1)]
    [TestCase(true, true, 0)]
    public async Task CatalogSpellingAcknowledgesOnlyItsCapturedLocalFile(
        bool refresh, bool removeUnacknowledgedFile, int expectedUnsaved)
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var acknowledged = Path.Combine(paths.OutputDirectory, "receipt.bin");
            var unacknowledged = Path.Combine(paths.OutputDirectory, "RECEIPT.BIN");
            Directory.CreateDirectory(paths.OutputDirectory);
            await File.WriteAllBytesAsync(acknowledged, [1, 2, 3]);
            await File.WriteAllBytesAsync(unacknowledged, [4, 5, 6, 7, 8]);
            var catalog = new RunnerArtifactServiceListArtifactsResponse();
            EditorArtifactMetadata? committed = null;
            var client = new FakeRunnerArtifactClient(catalog)
            {
                UploadResponseFailure = upload => upload.Metadata!.Filename == "RECEIPT.BIN"
                    ? new RpcException(new Status(StatusCode.FailedPrecondition, "Unacknowledged other local file"))
                    : null,
                RewriteUploadArtifact = artifact =>
                {
                    artifact.Filename = "RECEIPT.BIN";
                    artifact.CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow);
                    committed = artifact.Clone();
                    return artifact;
                }
            };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            for (var i = 0; i < 5; i++) await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(committed, Is.Not.Null);
            if (refresh)
            {
                catalog.Artifacts.Add(committed!.Clone());
                await service.RefreshSessionAsync("session-1");
                await WaitForIdentityUploadsToFinishAsync(service);
            }
            Assert.That(await File.ReadAllBytesAsync(acknowledged), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(await File.ReadAllBytesAsync(unacknowledged), Is.EqualTo(new byte[] { 4, 5, 6, 7, 8 }));
            File.Delete(removeUnacknowledgedFile ? unacknowledged : acknowledged);
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(expectedUnsaved),
                "catalog spelling must never acknowledge the other case-distinct local file");
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task RetiredUploadRemainsUnsavedWithoutReissuingIdentityOnRescan()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new FakeRunnerArtifactClient(new()) { RejectAsRetiredUpload = true };
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "retired.bin"), [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(1));
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
            Assert.That(service.TakeUnsavedUploads("session-1"), Is.EqualTo(new UnsavedUploads(1, false)));
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task RemoteDeletionPreservesLiveAcknowledgedSnapshotIdentityUntilLocalChange()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "kept.bin");
            var client = new FakeRunnerArtifactClient(new()); // Refreshed remote catalog stays empty.
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(1));
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            await File.WriteAllBytesAsync(path, [4, 5, 6, 7]);
            var changed = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(changed.Metadata!.UploadId, Is.Not.EqualTo(original.Metadata!.UploadId));
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task UncertainSnapshotLimitBackpressuresNewFilesAndReclaimsOnlyMissingBindings()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new FakeRunnerArtifactClient(new()) { RejectAsRetiredUpload = true };
            var policy = new ArtifactUploadPolicy { MaximumUnacknowledgedSnapshots = 2 };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance, uploadPolicy: policy);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            for (var n = 0; n < 2; n++)
            {
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, $"uncertain-{n}.bin"), [1, 2, 3]);
                await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                await WaitForIdentityUploadsToFinishAsync(service);
            }
            var next = Path.Combine(paths.OutputDirectory, "next.bin");
            await File.WriteAllBytesAsync(next, [4, 5, 6]);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(2), "live uncertainty must not be evicted to admit another snapshot");
            File.Delete(Path.Combine(paths.OutputDirectory, "uncertain-0.bin"));
            client.RejectAsRetiredUpload = false;
            await service.RefreshSessionAsync("session-1");
            var admitted = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            Assert.That(admitted.Metadata!.Filename, Is.EqualTo("next.bin"));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(3));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task OwedRescanRetriesUncertainFilesAtCapacityWithoutAnExternalRefresh()
    {
        var root = CreateTempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = UploadIdentityPaths(root);
            var replies = 0;
            var client = new FakeRunnerArtifactClient(new())
            {
                UploadGate = release.Task,
                UploadResponseFailure = _ => Interlocked.Increment(ref replies) <= 4
                    ? new RpcException(new Status(StatusCode.Internal, "Response lost")) : null,
            };
            var logger = new ListLogger<ArtifactUploadService>();
            var policy = new ArtifactUploadPolicy { MaximumUnacknowledgedSnapshots = 1 };
            await using var service = new ArtifactUploadService("secret", client, logger, uploadPolicy: policy);
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "uncertain.bin"), [1, 2, 3]);
                var first = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "waiting.bin"), [4, 5, 6]);
                await WaitForUploadLogAsync(logger, "Artifact upload queue is full");
                release.TrySetResult();
                for (var n = 0; n < 4; n++)
                {
                    var retry = await client.WaitForUploadAsync(TimeSpan.FromSeconds(8));
                    Assert.That(retry.Metadata!.Filename, Is.EqualTo("uncertain.bin"));
                    Assert.That(retry.Metadata.UploadId, Is.EqualTo(first.Metadata!.UploadId));
                }
                var admitted = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(admitted.Metadata!.Filename, Is.EqualTo("waiting.bin"));
                Assert.That(admitted.Metadata.UploadId, Is.Not.EqualTo(first.Metadata!.UploadId));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { release.TrySetResult(); }
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task RetiredReplyRequeuesAFileChangedWhileItsOriginalSnapshotWasPending()
    {
        var root = CreateTempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "rewritten.bin");
            var client = new FakeRunnerArtifactClient(new()) { UploadGate = release.Task, RejectAsRetiredUpload = true };
            var logger = new ListLogger<ArtifactUploadService>();
            await using var service = new ArtifactUploadService("secret", client, logger);
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                var prepared = await client.LastPreparedUploadResponse!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(prepared.Outcome, Is.EqualTo(RunnerArtifactUploadOutcome.RejectedRetiredUpload));
                await File.WriteAllBytesAsync(path, [9, 8, 7, 6, 5]);
                await WaitForUploadLogAsync(logger, "Artifact change coalesced with pending upload bytes=5");
                Assert.That(service.GetPendingUploadCount("session-1"), Is.EqualTo(1));
                // The first response was already constructed as retired. Only the
                // replacement snapshot may now receive a committed response.
                client.RejectAsRetiredUpload = false;
                release.TrySetResult();
                var replacement = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(replacement.Metadata!.UploadId, Is.Not.EqualTo(original.Metadata!.UploadId));
                Assert.That(replacement.ChunkBytes, Is.EqualTo(5));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { release.TrySetResult(); }
        }
        finally { TryDelete(root); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedSnapshotAfterFinalReplyLossGetsItsOwnRetries(bool retired)
    {
        var root = CreateTempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "last-retry.bin");
            var replies = 0;
            var client = new FakeRunnerArtifactClient(new()) { RejectAsRetiredUpload = retired };
            client.UploadResponseFailure = _ =>
            {
                var reply = Interlocked.Increment(ref replies);
                // The current call captured its gate when it started. Hold only
                // the fourth response, after all retries of the old snapshot.
                if (reply == 3) client.UploadGate = release.Task;
                return reply <= 3
                    ? new RpcException(new Status(StatusCode.Internal, "Response lost")) : null;
            };
            var logger = new ListLogger<ArtifactUploadService>();
            await using var service = new ArtifactUploadService("secret", client, logger);
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                for (var n = 0; n < 3; n++)
                {
                    var retry = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                    Assert.That(retry.Metadata!.UploadId, Is.EqualTo(original.Metadata!.UploadId));
                }
                var prepared = await client.LastPreparedUploadResponse!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(prepared.Outcome, Is.EqualTo(retired
                    ? RunnerArtifactUploadOutcome.RejectedRetiredUpload : RunnerArtifactUploadOutcome.Committed));
                await File.WriteAllBytesAsync(path, [9, 8, 7, 6, 5]);
                await WaitForUploadLogAsync(logger, "Artifact change coalesced with pending upload bytes=5");
                Assert.That(service.GetPendingUploadCount("session-1"), Is.EqualTo(1));
                client.RejectAsRetiredUpload = false;
                release.TrySetResult();
                var replacement = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(replacement.Metadata!.UploadId, Is.Not.EqualTo(original.Metadata!.UploadId));
                Assert.That(replacement.ChunkBytes, Is.EqualTo(5));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { release.TrySetResult(); }
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task RemoteDeletionOfAnEvictedUploadDoesNotCountItsPlaceholderAsSaved()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = CreateEvictionPaths(root, sealedSession: true);
            var path = Path.Combine(paths.OutputDirectory, "evicted.bin");
            // The upload succeeds, but the next authoritative listing is empty.
            var client = new FakeRunnerArtifactClient(new());
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(await EvictWhenRecordedAsync(service, "session-1"), Is.EqualTo(3));
            Assert.That(new FileInfo(path).Length, Is.Zero);
            // This current-session snapshot refresh reloads the catalog without
            // starting a new output scan that could replace the lost evidence.
            await service.SeedSessionAsync("session-1", "editor-1", "owner-1", null, CancellationToken.None);
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1),
                "the placeholder no longer stands for bytes held by the control plane");
        }
        finally { TryDelete(root); }
    }

    [TestCase("issued-sequence:42", true)]
    [TestCase("01ARZ3NDEKTSV4RRFFQ69G5FAV", true)]
    [TestCase("6d6a1e13-f5ea-4ec6-99e4-3db5983ac487", true)]
    [TestCase("", false)]
    [TestCase("   ", false)]
    public async Task ServerIssuedUploadIdentityIsOpaqueAndNonWhitespace(string uploadId, bool valid)
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var replies = 0;
            var client = new FakeRunnerArtifactClient(new())
            {
                IssuedUploadId = uploadId,
                UploadResponseFailure = _ => Interlocked.Increment(ref replies) == 1
                    ? new RpcException(new Status(StatusCode.Internal, "First response lost")) : null,
            };
            var logger = new ListLogger<ArtifactUploadService>();
            await using var service = new ArtifactUploadService("secret", client, logger);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "opaque.bin"), [1, 2, 3]);
            if (valid)
            {
                var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                var retry = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(original.Metadata!.UploadId, Is.EqualTo(uploadId));
                Assert.That(retry.Metadata!.UploadId, Is.EqualTo(uploadId));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(1));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            else
            {
                await WaitForUploadLogAsync(logger, "Failed to upload artifact");
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.UploadCount, Is.Zero);
                Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
            }
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task RemoteDeletionOfLiveUploadedBytesRetainsIdentityButCountsThemUnsaved()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "deleted-remotely.bin");
            var client = new FakeRunnerArtifactClient(new());
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
            await WaitForIdentityUploadsToFinishAsync(service);
            await service.RefreshSessionAsync("session-1");
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.IssuedIdentityCount, Is.EqualTo(1), "remote deletion cannot reissue an unchanged snapshot");
            Assert.That(client.UploadCount, Is.EqualTo(1));
            Assert.That(await File.ReadAllBytesAsync(path), Is.EqualTo(new byte[] { 1, 2, 3 }));
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1),
                "retaining the old ID does not prove remote bytes still exist");
            Assert.That(service.TakeUnsavedUploads("session-1"), Is.EqualTo(new UnsavedUploads(1, false)));
        }
        finally { TryDelete(root); }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ChangedSnapshotSurvivesTheFinalAttemptTimeout(bool expireTotalAllowance)
    {
        var root = CreateTempDirectory();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = UploadIdentityPaths(root);
            var path = Path.Combine(paths.OutputDirectory, "timeout-rewrite.bin");
            var clock = new ArtifactUploadBudgetTests.UploadClock();
            var policy = new ArtifactUploadPolicy
            {
                Clock = clock,
                MetadataTimeout = TimeSpan.FromSeconds(2),
                NoProgressTimeout = TimeSpan.FromSeconds(2),
                ReceiptTimeout = TimeSpan.FromSeconds(2)
            };
            var replies = 0;
            var client = new FakeRunnerArtifactClient(new());
            client.UploadResponseFailure = _ =>
            {
                var reply = Interlocked.Increment(ref replies);
                if (reply == 3) client.UploadGate = release.Task;
                return reply <= 3
                    ? new RpcException(new Status(StatusCode.Internal, "Response lost")) : null;
            };
            var logger = new ListLogger<ArtifactUploadService>();
            await using var service = new ArtifactUploadService("secret", client, logger, uploadPolicy: policy);
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                await File.WriteAllBytesAsync(path, [1, 2, 3]);
                var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                for (var n = 0; n < 3; n++)
                {
                    var retry = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                    Assert.That(retry.Metadata!.UploadId, Is.EqualTo(original.Metadata!.UploadId));
                }
                await client.LastPreparedUploadResponse!.WaitAsync(TimeSpan.FromSeconds(5));
                await File.WriteAllBytesAsync(path, [9, 8, 7, 6, 5]);
                await WaitForUploadLogAsync(logger, "Artifact change coalesced with pending upload bytes=5");
                client.UploadGate = null; // Only the old fourth call retains its stalled response.
                clock.Advance(TimeSpan.FromSeconds(expireTotalAllowance ? 6 : 3));
                var replacement = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(replacement.Metadata!.UploadId, Is.Not.EqualTo(original.Metadata!.UploadId));
                Assert.That(replacement.ChunkBytes, Is.EqualTo(5));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
                Assert.That(client.UploadCallsCancelled, Is.EqualTo(1), "the actual fourth call token must expire");
                Assert.That(client.UploadCallsDisposed, Is.EqualTo(client.UploadCallsStarted));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { release.TrySetResult(); }
        }
        finally { TryDelete(root); }
    }

    [Test]
    public async Task CapacityExhaustionReleasesExcessPendingWorkBeforeRetryingUncertainUploads()
    {
        var root = CreateTempDirectory();
        var issue = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new FakeRunnerArtifactClient(new()) { IssueIdentityGate = issue.Task };
            var replies = 0;
            client.UploadResponseFailure = _ =>
            {
                if (Interlocked.Increment(ref replies) != 1) return null;
                client.UploadGate = release.Task; // Hold the retry that can free capacity.
                return new RpcException(new Status(StatusCode.Internal, "First response lost"));
            };
            var policy = new ArtifactUploadPolicy { MaximumUnacknowledgedSnapshots = 1 };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance, uploadPolicy: policy);
            try
            {
                await service.StartSessionAsync("session-1", paths, CancellationToken.None);
                await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "uncertain.bin"), [1, 2, 3]);
                await client.IssueIdentityEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Both files enter the pending queue before the first issued
                // identity is bound, so admission cannot yet see the full set.
                await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "excess.bin"), [4, 5, 6]);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (service.GetPendingUploadCount("session-1") != 2)
                    await Task.Delay(10, timeout.Token);
                issue.TrySetResult();
                var original = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                var retry = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(retry.Metadata!.UploadId, Is.EqualTo(original.Metadata!.UploadId));
                await client.LastPreparedUploadResponse!.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.That(service.GetPendingUploadCount("session-1"), Is.EqualTo(1),
                    "excess capacity work must release its pending entry instead of requeuing an I/O retry");
                release.TrySetResult();
                var admitted = await client.WaitForUploadAsync(TimeSpan.FromSeconds(5));
                Assert.That(admitted.Metadata!.Filename, Is.EqualTo("excess.bin"));
                Assert.That(admitted.Metadata.UploadId, Is.Not.EqualTo(original.Metadata.UploadId));
                await WaitForIdentityUploadsToFinishAsync(service);
                Assert.That(client.IssuedIdentityCount, Is.EqualTo(2));
                Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
            }
            finally { issue.TrySetResult(); release.TrySetResult(); }
        }
        finally { TryDelete(root); }
    }

    private static async Task WaitForUploadLogAsync(ListLogger<ArtifactUploadService> logger, string text)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!logger.Snapshot().Any(entry => entry.Message.Contains(text, StringComparison.Ordinal)))
            await Task.Delay(10, timeout.Token);
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

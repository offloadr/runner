using System.Collections.Concurrent;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Core.Sealing;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class ArtifactUploadServiceTests
{
    [TestCase("metadata", false)]
    [TestCase("body", false)]
    [TestCase("completion", false)]
    [TestCase("reply", false)]
    [TestCase("metadata", true)]
    [TestCase("body", true)]
    [TestCase("completion", true)]
    [TestCase("reply", true)]
    public async Task StalledUploadCancelsItsCallAndAllowsAnotherFile(string phase, bool sealedTransfer)
    {
        var root = CreateTempDirectory();
        using var recipient = SealingKeyPair.Generate("output-1");
        using var sealing = SessionTransferSealing.Create("session-1",
            new SessionSealing { OutputRecipient = SessionTransferSealingTests.ToMessage(recipient.PublicKey) }, memoryWorkspace: true);
        try
        {
            var paths = UploadIdentityPaths(root) with { TransferSealing = sealedTransfer ? sealing : null };
            var client = new BudgetArtifactClient(phase);
            var policy = new ArtifactUploadPolicy
            {
                MetadataTimeout = TimeSpan.FromSeconds(2),
                NoProgressTimeout = TimeSpan.FromMilliseconds(300),
                ReceiptTimeout = TimeSpan.FromMilliseconds(300)
            };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance, uploadPolicy: policy);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "blocked.bin"), new byte[250_000]);
            await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "other.bin"), [1, 2, 3]);
            await client.OtherSaved.Task.WaitAsync(TimeSpan.FromSeconds(6));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (service.GetPendingUploadCount("session-1") != 0)
                await Task.Delay(10, timeout.Token);

            Assert.Multiple(() =>
            {
                Assert.That(client.CancelledCalls, Is.GreaterThan(0), "expiry must cancel the actual call token");
                Assert.That(client.StartedCalls, Is.EqualTo(client.DisposedCalls));
                Assert.That(client.BlockedIds.Distinct().Count(), Is.EqualTo(1), "retry must retain the exact snapshot identity");
            });
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
            Assert.That(service.TakeUnsavedUploads("session-1"), Is.EqualTo(new UnsavedUploads(1, false)));
            Assert.That(service.TakeUnsavedUploads("session-1"), Is.EqualTo(new UnsavedUploads(0, false)));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task MissingEditorIdentityExhaustsPendingWorkWithoutReportingItSaved()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new BudgetArtifactClient("reply");
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "blocked.bin"), [1, 2, 3]);
            await using var service = new ArtifactUploadService("secret", client, NullLogger<ArtifactUploadService>.Instance);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "", "owner-1", CancellationToken.None);
            Assert.That(service.GetPendingUploadCount("session-1"), Is.EqualTo(1));
            await WaitForIdentityUploadsToFinishAsync(service);
            Assert.That(client.StartedCalls, Is.Zero);
            Assert.That(await service.StopSessionAsync("session-1"), Is.EqualTo(1));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    public async Task InitialFileCreationCannotUnderestimateTheFirstUploadSnapshot()
    {
        var root = CreateTempDirectory();
        try
        {
            var paths = UploadIdentityPaths(root);
            var client = new BudgetArtifactClient("slow-body");
            var policy = new ArtifactUploadPolicy
            {
                MetadataTimeout = TimeSpan.FromSeconds(1),
                NoProgressTimeout = TimeSpan.FromMilliseconds(500),
                ReceiptTimeout = TimeSpan.FromSeconds(1),
                MinimumBytesPerSecond = 1024 * 1024
            };
            await using var service = new ArtifactUploadService("secret", client,
                NullLogger<ArtifactUploadService>.Instance, uploadPolicy: policy);
            await service.StartSessionAsync("session-1", paths, CancellationToken.None);
            await service.ActivateSessionAsync("session-1", "editor-1", "owner-1", CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(paths.OutputDirectory, "blocked.bin"), []);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (service.GetPendingUploadCount("session-1") == 0)
                await Task.Delay(1, timeout.Token);
            // The watcher has admitted the empty file. The first actual snapshot is
            // taken only after the editor finishes it and the stability check completes.
            await using (var file = File.OpenWrite(Path.Combine(paths.OutputDirectory, "blocked.bin")))
            {
                file.SetLength(8 * 1024 * 1024);
            }
            while (service.GetPendingUploadCount("session-1") != 0)
                await Task.Delay(10, timeout.Token);
            Assert.That(client.BlockedSizes, Is.EqualTo(new long[] { 8 * 1024 * 1024 }), "the first RPC must name the completed snapshot");
            Assert.That(client.CancelledCalls, Is.Zero, "the actual snapshot has time for a progressing transfer");
            Assert.That(client.StartedCalls, Is.EqualTo(1));
            Assert.That(await service.StopSessionAsync("session-1"), Is.Zero);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private sealed class BudgetArtifactClient(string blockedPhase) : RunnerArtifactService.RunnerArtifactServiceClient
    {
        private int _started;
        private int _disposed;
        private int _cancelled;
        public int StartedCalls => Volatile.Read(ref _started);
        public int DisposedCalls => Volatile.Read(ref _disposed);
        public int CancelledCalls => Volatile.Read(ref _cancelled);
        public ConcurrentQueue<string> BlockedIds { get; } = new();
        public ConcurrentQueue<long> BlockedSizes { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OtherSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<RunnerArtifactServiceListArtifactsResponse> ListArtifactsAsync(
            RunnerArtifactServiceListArtifactsRequest request, CallOptions options) => new(
                Task.FromResult(new RunnerArtifactServiceListArtifactsResponse()), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });

        public override AsyncClientStreamingCall<RunnerArtifactServiceUploadArtifactRequest, RunnerArtifactServiceUploadArtifactResponse>
            UploadArtifact(CallOptions options)
        {
            Interlocked.Increment(ref _started);
            var response = new TaskCompletionSource<RunnerArtifactServiceUploadArtifactResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            var never = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer = new BudgetUploadWriter(this, blockedPhase, options.CancellationToken, response, never.Task);
            return new(writer, response.Task.WaitAsync(options.CancellationToken), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () =>
                {
                    // Cancellation callbacks can be unregistered by the waiter's disposal
                    // continuation before they run. Inspect the actual call token instead.
                    if (options.CancellationToken.IsCancellationRequested)
                        Interlocked.Increment(ref _cancelled);
                    Interlocked.Increment(ref _disposed);
                });
        }

        private sealed class BudgetUploadWriter(BudgetArtifactClient owner, string phase, CancellationToken ct,
            TaskCompletionSource<RunnerArtifactServiceUploadArtifactResponse> response, Task never)
            : IClientStreamWriter<RunnerArtifactServiceUploadArtifactRequest>
        {
            private RunnerArtifactUploadMetadata? _metadata;
            private bool IsBlocked => _metadata?.Filename == "blocked.bin";
            public WriteOptions? WriteOptions { get; set; }

            public async Task WriteAsync(RunnerArtifactServiceUploadArtifactRequest message)
            {
                if (message.Metadata is { } metadata)
                {
                    _metadata = metadata.Clone();
                    if (IsBlocked)
                    {
                        owner.BlockedIds.Enqueue(metadata.UploadId);
                        owner.BlockedSizes.Enqueue(metadata.SizeBytes ?? -1);
                    }
                    if (IsBlocked && phase == "metadata") await BlockAsync();
                }
                else if (IsBlocked && phase == "body") await BlockAsync();
                else if (IsBlocked && phase == "slow-body") await Task.Delay(50, ct);
            }

            public async Task CompleteAsync()
            {
                if (IsBlocked && phase == "completion") await BlockAsync();
                if (IsBlocked && phase == "reply")
                {
                    owner.Entered.TrySetResult();
                    return;
                }
                response.TrySetResult(new RunnerArtifactServiceUploadArtifactResponse
                {
                    Outcome = RunnerArtifactUploadOutcome.Committed,
                    UploadId = _metadata!.UploadId,
                    Artifact = new()
                    {
                        EditorSid = _metadata.EditorSid,
                        Type = _metadata.Type,
                        Subfolder = _metadata.Subfolder,
                        Filename = _metadata.Filename,
                        SizeBytes = _metadata.SizeBytes,
                        CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow)
                    }
                });
                owner.OtherSaved.TrySetResult();
            }

            private async Task BlockAsync()
            {
                owner.Entered.TrySetResult();
                await never.WaitAsync(ct);
            }
        }
    }
}

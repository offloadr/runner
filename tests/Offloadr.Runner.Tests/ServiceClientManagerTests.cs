namespace Offloadr.Runner.Tests;

using Google.Protobuf;
using Grpc.Core;
using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    private const string PromptRunnerId = "11111111111141118111111111111111";
    private const string PromptSessionId = "22222222222242228222222222222222";
    private const string PromptSubmissionId = "44444444444444448444444444444444";

    private static SubmitPromptCommand WithPromptIdentity(SubmitPromptCommand prompt)
    {
        prompt.CommandId = "55555555555545558555555555555555";
        prompt.NativeIdentifiers = new() { SessionHash = "session-hash" };
        prompt.Target = new()
        {
            EditorLifecycleId = "cpu-1",
            EditorGeneration = 1,
            CpuRuntimeGeneration = 1,
            CpuRuntimeInstanceId = "66666666666646668666666666666666",
            GpuLifecycleId = "gpu-1",
            GpuGeneration = 1,
            AllocationId = "allocation-1",
            RuntimeEpoch = 1,
            RuntimeInstanceId = "77777777777747778777777777777777",
            RunnerId = PromptRunnerId,
            RunnerSessionId = PromptSessionId,
        };
        return prompt;
    }

    private static Task<GrantPromptExecutionResponse> GrantPromptForTestAsync(GrantPromptExecutionRequest request, CancellationToken token)
        => Task.FromResult(new GrantPromptExecutionResponse
        {
            MayExecute = true,
            Submission = new()
            {
                SubmissionId = request.SubmissionId,
                Target = request.Target.Clone(),
                ExecutionPermissionUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
            },
        });

    [Test]
    public async Task AcknowledgeTransientRequestWithRetryAsync_RetriesUnavailableRpc()
    {
        var attempts = 0;
        var response = new AcknowledgeEditorRuntimeRequestRequest { RequestId = "request-1" };

        var acknowledged = await ServiceClientManager.AcknowledgeTransientRequestWithRetryAsync(
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "temporarily unavailable")))
                    : Task.CompletedTask;
            },
            response,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(acknowledged, Is.True);
            Assert.That(attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AcknowledgeRuntimeCommandWithRetryAsync_RetriesUnavailableRpc()
    {
        var attempts = 0;
        var response = new AcknowledgeEditorRuntimeQuiesceRequest { CommandId = "command-1" };

        var acknowledged = await ServiceClientManager.AcknowledgeRuntimeCommandWithRetryAsync(
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? Task.FromException(new RpcException(new Status(StatusCode.Unavailable, "temporarily unavailable")))
                    : Task.CompletedTask;
            },
            response,
            response.CommandId,
            CancellationToken.None,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(1));

        Assert.Multiple(() =>
        {
            Assert.That(acknowledged, Is.True);
            Assert.That(attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AcknowledgeRuntimeCommandWithRetryAsync_BoundsRetainedEvidenceAttempts()
    {
        var attempts = 0;
        var acknowledged = await ServiceClientManager.AcknowledgeRuntimeCommandWithRetryAsync(
            (_, _) =>
            {
                attempts++;
                throw new RpcException(new(StatusCode.Unavailable, "evidence path down"));
            },
            new ReportPromptEvidenceRequest(), "command-1", CancellationToken.None,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), maxAttempts: 3);
        Assert.Multiple(() =>
        {
            Assert.That(acknowledged, Is.False);
            Assert.That(attempts, Is.EqualTo(3));
        });
    }

    [Test]
    public void RuntimeCommandDeduplicationCache_RejectsDuplicateCommand()
    {
        var cache = new ServiceClientManager.RuntimeCommandDeduplicationCache();

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryAdd("command-1"), Is.True);
            Assert.That(cache.TryAdd("command-1"), Is.False);
            Assert.That(cache.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void RuntimeCommandDeduplicationCache_EvictsOldestCommandAtEntryLimit()
    {
        var cache = new ServiceClientManager.RuntimeCommandDeduplicationCache(maxEntries: 2);

        Assert.That(cache.TryAdd("command-1"), Is.True);
        Assert.That(cache.TryAdd("command-2"), Is.True);
        Assert.That(cache.TryAdd("command-3"), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryAdd("command-1"), Is.True);
            Assert.That(cache.TryAdd("command-3"), Is.False);
            Assert.That(cache.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void TransientAcknowledgementCache_EvictsOldestResponseAtByteLimit()
    {
        var first = new AcknowledgeEditorRuntimeRequestRequest
        {
            RequestId = "request-1",
            Body = ByteString.CopyFrom(new byte[64])
        };
        var second = new AcknowledgeEditorRuntimeRequestRequest
        {
            RequestId = "request-2",
            Body = ByteString.CopyFrom(new byte[64])
        };
        var cache = new ServiceClientManager.TransientAcknowledgementCache(
            maxEntries: 10,
            maxBytes: first.CalculateSize());

        cache.Set(first);
        cache.Set(second);

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryGet(first.RequestId, out _), Is.False);
            Assert.That(cache.TryGet(second.RequestId, out var cached), Is.True);
            Assert.That(cached.Body.Length, Is.EqualTo(64));
            Assert.That(cache.Count, Is.EqualTo(1));
            Assert.That(cache.RetainedBytes, Is.LessThanOrEqualTo(first.CalculateSize()));
        });
    }

    [Test]
    public void TransientAcknowledgementCache_EvictsOldestResponseAtEntryLimit()
    {
        var cache = new ServiceClientManager.TransientAcknowledgementCache(maxEntries: 2, maxBytes: 1024);
        cache.Set(new AcknowledgeEditorRuntimeRequestRequest { RequestId = "request-1" });
        cache.Set(new AcknowledgeEditorRuntimeRequestRequest { RequestId = "request-2" });
        cache.Set(new AcknowledgeEditorRuntimeRequestRequest { RequestId = "request-3" });

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryGet("request-1", out _), Is.False);
            Assert.That(cache.TryGet("request-2", out _), Is.True);
            Assert.That(cache.TryGet("request-3", out _), Is.True);
            Assert.That(cache.Count, Is.EqualTo(2));
        });
    }

    [Test]
    public void TransientAcknowledgementCache_RemoveReleasesEntryAndBytes()
    {
        var response = new AcknowledgeEditorRuntimeRequestRequest
        {
            RequestId = "request-1",
            Body = ByteString.CopyFrom(new byte[64])
        };
        var cache = new ServiceClientManager.TransientAcknowledgementCache();
        cache.Set(response);

        cache.Remove(response.RequestId);

        Assert.Multiple(() =>
        {
            Assert.That(cache.TryGet(response.RequestId, out _), Is.False);
            Assert.That(cache.Count, Is.Zero);
            Assert.That(cache.RetainedBytes, Is.Zero);
        });
    }

    [TestCase("comfyui", "POST", "api/prompt", true)]
    [TestCase("comfyui", "POST", "prompt", true)]
    [TestCase("comfyui", "POST", "api/interrupt", false)]
    [TestCase("comfyui", "GET", "api/history", false)]
    [TestCase("forge-neo", "POST", "queue/join", true)]
    public void ShouldPrepareGenerationDependencies_OnlyPreparesComfyPromptSubmissions(
        string runtimeKind,
        string method,
        string path,
        bool expected)
    {
        var command = new SubmitPromptCommand
        {
            EditorRuntimeKind = runtimeKind,
            HttpMethod = method,
            HttpPath = path
        };

        Assert.That(ServiceClientManager.ShouldPrepareGenerationDependencies(command), Is.EqualTo(expected));
    }

    [Test]
    public void ComputeLocalModelLoopDelay_PicksKeepAliveWhenSoonerThanScan()
    {
        var now = new DateTime(2026, 2, 28, 17, 0, 0, DateTimeKind.Utc);
        var delay = ServiceClientManager.ComputeLocalModelLoopDelay(
            now,
            now.AddMinutes(5),
            now.AddMinutes(1));

        Assert.That(delay, Is.EqualTo(TimeSpan.FromMinutes(1)));
    }

    [Test]
    public void ComputeLocalModelLoopDelay_PicksScanWhenSoonerThanKeepAlive()
    {
        var now = new DateTime(2026, 2, 28, 17, 0, 0, DateTimeKind.Utc);
        var delay = ServiceClientManager.ComputeLocalModelLoopDelay(
            now,
            now.AddSeconds(30),
            now.AddMinutes(1));

        Assert.That(delay, Is.EqualTo(TimeSpan.FromSeconds(30)));
    }

    [Test]
    public void ComputeLocalModelLoopDelay_ClampsNegativeDelayToZero()
    {
        var now = new DateTime(2026, 2, 28, 17, 0, 0, DateTimeKind.Utc);
        var delay = ServiceClientManager.ComputeLocalModelLoopDelay(
            now,
            now.AddSeconds(-10),
            now.AddMinutes(1));

        Assert.That(delay, Is.EqualTo(TimeSpan.Zero));
    }

    [Test]
    public async Task RunLocalModelSyncLoopAsync_RetriesRpcFailures_WithoutCancellingSession()
    {
        using var cts = new CancellationTokenSource();
        var firstFailureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAttemptObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;

        var loopTask = ServiceClientManager.RunLocalModelSyncLoopAsync(
            async (_, cancellationToken) =>
            {
                attempt++;
                if (attempt == 1)
                {
                    firstFailureObserved.TrySetResult();
                    throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.Unavailable, "transient"));
                }

                secondAttemptObserved.TrySetResult();
                await Task.Yield();
                return new Offloadr.Runner.V1.SyncLocalModelsResponse { AcceptedCount = 1 };
            },
            shutdown: cts.Token,
            runnerId: "runner-1",
            scanInterval: TimeSpan.FromMinutes(5),
            minBackoff: TimeSpan.FromMilliseconds(5),
            maxBackoff: TimeSpan.FromMilliseconds(5),
            snapshotProvider: () => CreateSnapshot("digest-1", "selection-1"),
            snapshotObserver: null,
            initialStatusApplied: Task.CompletedTask);

        await firstFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(cts.IsCancellationRequested, Is.False);

        await secondAttemptObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await loopTask;

        Assert.That(attempt, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task RunLocalModelSyncLoopAsync_RetriesSnapshotFailures_WithoutCancellingSession()
    {
        using var cts = new CancellationTokenSource();
        var firstFailureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var syncObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshotAttempt = 0;

        var loopTask = ServiceClientManager.RunLocalModelSyncLoopAsync(
            (_, _) =>
            {
                syncObserved.TrySetResult();
                return Task.FromResult(new Offloadr.Runner.V1.SyncLocalModelsResponse { AcceptedCount = 0 });
            },
            shutdown: cts.Token,
            runnerId: "runner-1",
            scanInterval: TimeSpan.FromMinutes(5),
            minBackoff: TimeSpan.FromMilliseconds(5),
            maxBackoff: TimeSpan.FromMilliseconds(5),
            snapshotProvider: () =>
            {
                snapshotAttempt++;
                if (snapshotAttempt == 1)
                {
                    firstFailureObserved.TrySetResult();
                    throw new InvalidOperationException("scan failed");
                }

                return CreateSnapshot("digest-2", "selection-2");
            },
            snapshotObserver: null,
            initialStatusApplied: Task.CompletedTask);

        await firstFailureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.That(cts.IsCancellationRequested, Is.False);

        await syncObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await loopTask;

        Assert.That(snapshotAttempt, Is.GreaterThanOrEqualTo(2));
    }

    [Test]
    public async Task RunLocalModelSyncLoopAsync_FullSyncsEmptyInventory()
    {
        using var cts = new CancellationTokenSource();
        var requestObserved = new TaskCompletionSource<Offloadr.Runner.V1.SyncLocalModelsRequest>(TaskCreationOptions.RunContinuationsAsynchronously);

        var loopTask = ServiceClientManager.RunLocalModelSyncLoopAsync(
            (request, _) =>
            {
                requestObserved.TrySetResult(request);
                return Task.FromResult(new Offloadr.Runner.V1.SyncLocalModelsResponse { AcceptedCount = 0 });
            },
            shutdown: cts.Token,
            runnerId: "runner-1",
            scanInterval: TimeSpan.FromMinutes(5),
            minBackoff: TimeSpan.FromMilliseconds(5),
            maxBackoff: TimeSpan.FromMilliseconds(5),
            snapshotProvider: () => CreateSnapshot("digest-empty"),
            snapshotObserver: null,
            initialStatusApplied: Task.CompletedTask);

        var request = await requestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cts.Cancel();
        await loopTask;

        Assert.Multiple(() =>
        {
            Assert.That(request.FullSnapshot, Is.True);
            Assert.That(request.SelectionHashes, Is.Empty);
            Assert.That(request.SnapshotDigest, Is.EqualTo(CreateSnapshot("digest-empty").SelectionDigest));
        });
    }

    [Test]
    public async Task RunLocalModelSyncLoopAsync_ForcesFullResync_AfterDigestOnlyRefreshRejected()
    {
        var originalKeepAlive = ServiceClientManager.LocalModelKeepAliveInterval;
        ServiceClientManager.LocalModelKeepAliveInterval = TimeSpan.FromMilliseconds(5);
        using var cts = new CancellationTokenSource();
        var firstFullSyncObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFullSyncObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<Offloadr.Runner.V1.SyncLocalModelsRequest>();

        try
        {
            var loopTask = ServiceClientManager.RunLocalModelSyncLoopAsync(
                (request, _) =>
                {
                    lock (requests)
                    {
                        requests.Add(request.Clone());
                        if (requests.Count == 1)
                        {
                            firstFullSyncObserved.TrySetResult();
                            return Task.FromResult(new Offloadr.Runner.V1.SyncLocalModelsResponse { AcceptedCount = 1 });
                        }

                        if (requests.Count == 2)
                        {
                            return Task.FromException<Offloadr.Runner.V1.SyncLocalModelsResponse>(
                                new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.InvalidArgument, "missing digest")));
                        }

                        secondFullSyncObserved.TrySetResult();
                        cts.Cancel();
                        return Task.FromResult(new Offloadr.Runner.V1.SyncLocalModelsResponse { AcceptedCount = 1 });
                    }
                },
                shutdown: cts.Token,
                runnerId: "runner-1",
                scanInterval: TimeSpan.FromMinutes(5),
                minBackoff: TimeSpan.FromMilliseconds(5),
                maxBackoff: TimeSpan.FromMilliseconds(5),
                snapshotProvider: () => CreateSnapshot("digest-1", "selection-1"),
                snapshotObserver: null,
                initialStatusApplied: Task.CompletedTask);

            await firstFullSyncObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await secondFullSyncObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await loopTask;
        }
        finally
        {
            ServiceClientManager.LocalModelKeepAliveInterval = originalKeepAlive;
        }

        Assert.That(requests, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            Assert.That(requests[0].FullSnapshot, Is.True);
            Assert.That(requests[0].SelectionHashes, Has.Count.EqualTo(1));
            Assert.That(requests[1].FullSnapshot, Is.False);
            Assert.That(requests[1].SelectionHashes, Is.Empty);
            Assert.That(requests[2].FullSnapshot, Is.True);
            Assert.That(requests[2].SelectionHashes, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public void PrepareInitialLocalModelSnapshot_ReturnsProjectedSnapshot()
    {
        LocalModelSnapshot? observedSnapshot = null;
        var expectedSnapshot = CreateSnapshot("digest-1", "selection-1");

        var snapshot = ServiceClientManager.PrepareInitialLocalModelSnapshot(
            () => expectedSnapshot,
            current => observedSnapshot = current);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.EqualTo(expectedSnapshot));
            Assert.That(observedSnapshot, Is.EqualTo(expectedSnapshot));
        });
    }

    [Test]
    public void PrepareInitialLocalModelSnapshot_ReturnsNull_WhenSnapshotPreparationFails()
    {
        var snapshot = ServiceClientManager.PrepareInitialLocalModelSnapshot(
            () => throw new InvalidOperationException("scan failed"),
            _ => Assert.Fail("observer should not run"));

        Assert.That(snapshot, Is.Null);
    }

    [Test]
    public void CreateSnapshot_ComputesDeterministicRemoteDigest()
    {
        var snapshot = CreateSnapshot("digest-1", "b", "a", "a");

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.SelectionHashes, Is.EqualTo(new[] { "a", "b" }));
            Assert.That(snapshot.SelectionDigest, Is.EqualTo(Offloadr.Common.V1.LocalModelSelectionHash.ComputeSnapshotDigest(["a", "b"])));
        });
    }

    [Test]
    public async Task PreemptActiveSessionIfNeededAsync_DoesNothing_WhenNoActiveSession()
    {
        var stopCalls = 0;
        var relayStopCalls = 0;
        var uploadStopCalls = 0;
        string? cancelledDownloadSession = null;
        string? downloadActive = null;

        var preempted = await ServiceClientManager.PreemptActiveSessionIfNeededAsync(
            "incoming",
            getActiveSessionId: () => string.Empty,
            stopSession: (_, _, _) =>
            {
                stopCalls++;
                return Task.CompletedTask;
            },
            stopSessionRelay: _ =>
            {
                relayStopCalls++;
                return Task.CompletedTask;
            },
            stopArtifactUploads: _ =>
            {
                uploadStopCalls++;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive ?? string.Empty,
            setDownloadActiveSessionId: value => downloadActive = value,
            cancellationToken: CancellationToken.None);

        Assert.That(preempted, Is.Null);
        Assert.That(stopCalls, Is.EqualTo(0));
        Assert.That(relayStopCalls, Is.EqualTo(0));
        Assert.That(uploadStopCalls, Is.EqualTo(0));
        Assert.That(cancelledDownloadSession, Is.Null);
        Assert.That(downloadActive, Is.Null);
    }

    [Test]
    public async Task PreemptActiveSessionIfNeededAsync_DoesNothing_WhenIncomingMatchesActive()
    {
        var stopCalls = 0;
        var relayStopCalls = 0;
        var uploadStopCalls = 0;
        string? downloadActive = "session-1";
        string? cancelledDownloadSession = null;
        var setCalls = 0;

        var preempted = await ServiceClientManager.PreemptActiveSessionIfNeededAsync(
            "session-1",
            getActiveSessionId: () => "session-1",
            stopSession: (_, _, _) =>
            {
                stopCalls++;
                return Task.CompletedTask;
            },
            stopSessionRelay: _ =>
            {
                relayStopCalls++;
                return Task.CompletedTask;
            },
            stopArtifactUploads: _ =>
            {
                uploadStopCalls++;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive ?? string.Empty,
            setDownloadActiveSessionId: value =>
            {
                setCalls++;
                downloadActive = value;
            },
            cancellationToken: CancellationToken.None);

        Assert.That(preempted, Is.Null);
        Assert.That(stopCalls, Is.EqualTo(0));
        Assert.That(relayStopCalls, Is.EqualTo(0));
        Assert.That(uploadStopCalls, Is.EqualTo(0));
        Assert.That(cancelledDownloadSession, Is.Null);
        Assert.That(setCalls, Is.EqualTo(0));
        Assert.That(downloadActive, Is.EqualTo("session-1"));
    }

    [Test]
    public async Task PreemptActiveSessionIfNeededAsync_StopsExistingSessionAndCleansResources()
    {
        var stopCalls = 0;
        string? relayStopped = null;
        string? uploadStopped = null;
        string? cancelledDownloadSession = null;
        string? downloadActive = "session-old";
        string? downloadSetValue = "session-old";
        Func<string, CancellationToken, Task>? beforeCleanup = null;

        var preempted = await ServiceClientManager.PreemptActiveSessionIfNeededAsync(
            "session-new",
            getActiveSessionId: () => "session-old",
            stopSession: (sessionId, callback, _) =>
            {
                stopCalls++;
                beforeCleanup = callback;
                return Task.CompletedTask;
            },
            stopSessionRelay: sessionId =>
            {
                relayStopped = sessionId;
                return Task.CompletedTask;
            },
            stopArtifactUploads: sessionId =>
            {
                uploadStopped = sessionId;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive ?? string.Empty,
            setDownloadActiveSessionId: value =>
            {
                downloadSetValue = value;
                downloadActive = value;
            },
            cancellationToken: CancellationToken.None);

        Assert.That(beforeCleanup, Is.Not.Null);
        await beforeCleanup!("session-old", CancellationToken.None);
        Assert.That(preempted, Is.EqualTo("session-old"));
        Assert.That(stopCalls, Is.EqualTo(1));
        Assert.That(relayStopped, Is.EqualTo("session-old"));
        Assert.That(uploadStopped, Is.EqualTo("session-old"));
        Assert.That(cancelledDownloadSession, Is.EqualTo("session-old"));
        Assert.That(downloadSetValue, Is.Null);
    }

    [Test]
    public async Task PreemptActiveSessionIfNeededAsync_DoesNotClearDownload_WhenDifferentSessionIsActiveForDownloads()
    {
        string? downloadActive = "another-session";
        string? cancelledDownloadSession = null;
        var setCalls = 0;

        await ServiceClientManager.PreemptActiveSessionIfNeededAsync(
            "session-new",
            getActiveSessionId: () => "session-old",
            stopSession: (_, _, _) => Task.CompletedTask,
            stopSessionRelay: _ => Task.CompletedTask,
            stopArtifactUploads: _ => Task.CompletedTask,
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive ?? string.Empty,
            setDownloadActiveSessionId: _ => setCalls++,
            cancellationToken: CancellationToken.None);

        Assert.That(setCalls, Is.EqualTo(0));
        Assert.That(cancelledDownloadSession, Is.Null);
        Assert.That(downloadActive, Is.EqualTo("another-session"));
    }

    [Test]
    public async Task PreemptActiveSessionIfNeededAsync_Continues_WhenResourceCleanupFails()
    {
        var stopCalls = 0;
        string? cancelledDownloadSession = null;

        var preempted = await ServiceClientManager.PreemptActiveSessionIfNeededAsync(
            "session-new",
            getActiveSessionId: () => "session-old",
            stopSession: (_, callback, _) =>
            {
                stopCalls++;
                if (callback is not null)
                {
                    return callback("session-old", CancellationToken.None);
                }
                return Task.CompletedTask;
            },
            stopSessionRelay: _ => throw new InvalidOperationException("relay failed"),
            stopArtifactUploads: _ => throw new InvalidOperationException("uploads failed"),
            stopWorkspaceMirrors: _ => throw new InvalidOperationException("workspace mirror failed"),
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => "session-old",
            setDownloadActiveSessionId: _ => { },
            cancellationToken: CancellationToken.None);

        Assert.That(preempted, Is.EqualTo("session-old"));
        Assert.That(stopCalls, Is.EqualTo(1));
        Assert.That(cancelledDownloadSession, Is.EqualTo("session-old"));
    }

    [Test]
    public async Task AbortRevokedSessionAsync_StopsMatchingActiveSession_AndCleansResources()
    {
        string? relayStopped = null;
        string? uploadStopped = null;
        string? stoppedSession = null;
        string? downloadActive = "session-1";
        string? cancelledDownloadSession = null;

        var aborted = await ServiceClientManager.AbortRevokedSessionAsync(
            "session-1",
            getActiveSessionId: () => "session-1",
            stopSession: (sessionId, callback, _) =>
            {
                stoppedSession = sessionId;
                return callback is null ? Task.CompletedTask : callback(sessionId, CancellationToken.None);
            },
            stopSessionRelay: sessionId =>
            {
                relayStopped = sessionId;
                return Task.CompletedTask;
            },
            stopArtifactUploads: sessionId =>
            {
                uploadStopped = sessionId;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive,
            setDownloadActiveSessionId: value => downloadActive = value);

        Assert.That(aborted, Is.True);
        Assert.That(stoppedSession, Is.EqualTo("session-1"));
        Assert.That(relayStopped, Is.EqualTo("session-1"));
        Assert.That(uploadStopped, Is.EqualTo("session-1"));
        Assert.That(cancelledDownloadSession, Is.EqualTo("session-1"));
        Assert.That(downloadActive, Is.Null);
    }

    [Test]
    public async Task AbortRevokedSessionAsync_DoesNotStopDifferentActiveSession()
    {
        var stopCalls = 0;
        string? relayStopped = null;
        string? uploadStopped = null;
        string? downloadActive = "session-1";
        string? cancelledDownloadSession = null;

        var aborted = await ServiceClientManager.AbortRevokedSessionAsync(
            "session-1",
            getActiveSessionId: () => "session-2",
            stopSession: (_, _, _) =>
            {
                stopCalls++;
                return Task.CompletedTask;
            },
            stopSessionRelay: sessionId =>
            {
                relayStopped = sessionId;
                return Task.CompletedTask;
            },
            stopArtifactUploads: sessionId =>
            {
                uploadStopped = sessionId;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive,
            setDownloadActiveSessionId: value => downloadActive = value);

        Assert.That(aborted, Is.False);
        Assert.That(stopCalls, Is.EqualTo(0));
        Assert.That(relayStopped, Is.EqualTo("session-1"));
        Assert.That(uploadStopped, Is.EqualTo("session-1"));
        Assert.That(cancelledDownloadSession, Is.EqualTo("session-1"));
        Assert.That(downloadActive, Is.Null);
    }

    [Test]
    public async Task StopSessionAndCleanupAsync_CancelsDownloads_AndStopsResources()
    {
        string? relayStopped = null;
        string? uploadStopped = null;
        string? stoppedSession = null;
        string? cancelledDownloadSession = null;
        string? downloadActive = "session-1";
        var callOrder = new List<string>();

        await ServiceClientManager.StopSessionAndCleanupAsync(
            "session-1",
            stopSession: (sessionId, callback, _) =>
            {
                callOrder.Add("session");
                stoppedSession = sessionId;
                return callback is null ? Task.CompletedTask : callback(sessionId, CancellationToken.None);
            },
            stopSessionRelay: sessionId =>
            {
                callOrder.Add("relay");
                relayStopped = sessionId;
                return Task.CompletedTask;
            },
            stopArtifactUploads: sessionId =>
            {
                callOrder.Add("uploads");
                uploadStopped = sessionId;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: sessionId =>
            {
                callOrder.Add("mirror");
                return Task.CompletedTask;
            },
            cancelSessionDownloads: sessionId =>
            {
                callOrder.Add("downloads");
                cancelledDownloadSession = sessionId;
            },
            getDownloadActiveSessionId: () => downloadActive,
            setDownloadActiveSessionId: value => downloadActive = value,
            cancellationToken: CancellationToken.None);

        Assert.That(stoppedSession, Is.EqualTo("session-1"));
        Assert.That(relayStopped, Is.EqualTo("session-1"));
        Assert.That(uploadStopped, Is.EqualTo("session-1"));
        Assert.That(cancelledDownloadSession, Is.EqualTo("session-1"));
        Assert.That(downloadActive, Is.Null);
        Assert.That(
            callOrder,
            Is.EqualTo(new[] { "downloads", "session", "uploads", "mirror", "downloads", "relay" }));
    }

    [Test]
    public async Task StopSessionAndCleanupAsync_DoesNotClearDifferentActiveDownloadSession()
    {
        string? cancelledDownloadSession = null;
        string? downloadActive = "session-2";

        await ServiceClientManager.StopSessionAndCleanupAsync(
            "session-1",
            stopSession: (_, callback, _) => callback is null ? Task.CompletedTask : callback("session-1", CancellationToken.None),
            stopSessionRelay: _ => Task.CompletedTask,
            stopArtifactUploads: _ => Task.CompletedTask,
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive,
            setDownloadActiveSessionId: value => downloadActive = value,
            cancellationToken: CancellationToken.None);

        Assert.That(cancelledDownloadSession, Is.EqualTo("session-1"));
        Assert.That(downloadActive, Is.EqualTo("session-2"));
    }

    [Test]
    public void StopSessionAndCleanupAsync_StopsSidecarsAndPropagates_WhenStopSessionThrowsBeforeCallback()
    {
        string? uploadStopped = null;
        string? mirrorStopped = null;

        Assert.CatchAsync<OperationCanceledException>(() => ServiceClientManager.StopSessionAndCleanupAsync(
            "session-1",
            stopSession: (_, _, _) => throw new OperationCanceledException("stream dropped"),
            stopSessionRelay: _ => Task.CompletedTask,
            stopArtifactUploads: sessionId =>
            {
                uploadStopped = sessionId;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: sessionId =>
            {
                mirrorStopped = sessionId;
                return Task.CompletedTask;
            },
            cancelSessionDownloads: _ => { },
            getDownloadActiveSessionId: () => "session-1",
            setDownloadActiveSessionId: _ => { },
            cancellationToken: new CancellationToken(canceled: true)));

        Assert.That(uploadStopped, Is.EqualTo("session-1"));
        Assert.That(mirrorStopped, Is.EqualTo("session-1"));
    }

    [Test]
    public async Task CleanupExitedSessionAsync_CancelsDownloads_AndStopsSidecars()
    {
        string? relayStopped = null;
        string? uploadStopped = null;
        string? mirrorStopped = null;
        string? cancelledDownloadSession = null;
        string? downloadActive = "session-1";
        var callOrder = new List<string>();

        await ServiceClientManager.CleanupExitedSessionAsync(
            " session-1 ",
            stopSessionRelay: sessionId =>
            {
                callOrder.Add("relay");
                relayStopped = sessionId;
                return Task.CompletedTask;
            },
            stopArtifactUploads: sessionId =>
            {
                callOrder.Add("uploads");
                uploadStopped = sessionId;
                return Task.CompletedTask;
            },
            stopWorkspaceMirrors: sessionId =>
            {
                callOrder.Add("mirror");
                mirrorStopped = sessionId;
                return Task.CompletedTask;
            },
            cancelSessionDownloads: sessionId =>
            {
                callOrder.Add("downloads");
                cancelledDownloadSession = sessionId;
            },
            getDownloadActiveSessionId: () => downloadActive,
            setDownloadActiveSessionId: value => downloadActive = value,
            cancellationToken: CancellationToken.None);

        Assert.That(relayStopped, Is.EqualTo("session-1"));
        Assert.That(uploadStopped, Is.EqualTo("session-1"));
        Assert.That(mirrorStopped, Is.EqualTo("session-1"));
        Assert.That(cancelledDownloadSession, Is.EqualTo("session-1"));
        Assert.That(downloadActive, Is.Null);
        Assert.That(callOrder, Is.EqualTo(new[] { "downloads", "uploads", "mirror", "relay" }));
    }

    [Test]
    public async Task CleanupExitedSessionAsync_DoesNotClearDifferentActiveDownloadSession()
    {
        string? cancelledDownloadSession = null;
        string? downloadActive = "session-2";

        await ServiceClientManager.CleanupExitedSessionAsync(
            "session-1",
            stopSessionRelay: _ => Task.CompletedTask,
            stopArtifactUploads: _ => Task.CompletedTask,
            stopWorkspaceMirrors: _ => Task.CompletedTask,
            cancelSessionDownloads: sessionId => cancelledDownloadSession = sessionId,
            getDownloadActiveSessionId: () => downloadActive,
            setDownloadActiveSessionId: value => downloadActive = value,
            cancellationToken: CancellationToken.None);

        Assert.That(cancelledDownloadSession, Is.EqualTo("session-1"));
        Assert.That(downloadActive, Is.EqualTo("session-2"));
    }

    [Test]
    public void ClearRuntimeSessionState_ClearsHeartbeatAndIdentityForExitedSession()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState("session-1");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 3, "runtime-1");

        ServiceClientManager.ClearRuntimeSessionState(logicalSession, runtimeIdentities, "session-1");

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.Empty);
            Assert.That(runtimeIdentities.TryGet("session-1", out _), Is.False);
        });
    }

    [Test]
    public void ClearRuntimeSessionState_DoesNotClearReplacementLogicalSession()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState("session-2");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 3, "runtime-1");

        ServiceClientManager.ClearRuntimeSessionState(logicalSession, runtimeIdentities, "session-1");

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.EqualTo("session-2"));
            Assert.That(runtimeIdentities.TryGet("session-1", out _), Is.False);
        });
    }

    [Test]
    public void ClearRuntimeSessionStateIfIdentityMatches_ClearsFailedLaunchIdentity()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState(string.Empty);
        logicalSession.SetActiveRuntime("session-1", 7, 3, "runtime-1");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 3, "runtime-1");

        ServiceClientManager.ClearRuntimeSessionStateIfIdentityMatches(
            logicalSession,
            runtimeIdentities,
            "session-1",
            7,
            3,
            "runtime-1");

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.Empty);
            Assert.That(runtimeIdentities.TryGet("session-1", out _), Is.False);
        });
    }

    [Test]
    public void TryAdoptRuntime_RefusesAReplacedSessionAtTheGenerationItRanAt()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(1, 1, "a-1"), 0, replaceOtherSession: true), Is.True);
            Assert.That(state.TryAdoptRuntime("session-b", new RuntimeIdentity(1, 1, "b-1"), 0, replaceOtherSession: true), Is.True);
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(1, 2, "a-2"), 0, replaceOtherSession: true), Is.False);
            Assert.That(state.GetActiveSessionId(), Is.EqualTo("session-b"));
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(2, 1, "a-3"), 0, replaceOtherSession: true), Is.True);
        });
    }

    [Test]
    public void TryAdoptRuntime_RefusesAStartOlderThanTheNewestAssignment()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);

        Assert.Multiple(() =>
        {
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(1, 1, "a-1"), 0, replaceOtherSession: true, assignmentSequence: 5), Is.True);
            Assert.That(state.TryAdoptRuntime("session-b", new RuntimeIdentity(1, 1, "b-1"), 0, replaceOtherSession: true, assignmentSequence: 7), Is.True);
            // Session c never ran here, but its assignment predates b's.
            Assert.That(state.TryAdoptRuntime("session-c", new RuntimeIdentity(1, 1, "c-1"), 0, replaceOtherSession: true, assignmentSequence: 6), Is.False);
            Assert.That(state.GetActiveSessionId(), Is.EqualTo("session-b"));
            // A redelivered start of the current assignment is still the same runtime.
            Assert.That(state.TryAdoptRuntime("session-b", new RuntimeIdentity(1, 1, "b-1"), 0, replaceOtherSession: true, assignmentSequence: 7), Is.True);
            Assert.That(state.TryAdoptRuntime("session-d", new RuntimeIdentity(1, 1, "d-1"), 0, replaceOtherSession: true, assignmentSequence: 8), Is.True);
            // Without a sequence (an older control plane) starts are not ordered across sessions.
            Assert.That(state.TryAdoptRuntime("session-e", new RuntimeIdentity(1, 1, "e-1"), 0, replaceOtherSession: true), Is.True);
        });
    }

    [Test]
    public void ClearAndRetire_RefusesALateStartForTheStoppedGeneration()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);
        state.TryAdoptRuntime("session-a", new RuntimeIdentity(3, 1, "a-1"), 0, replaceOtherSession: true);

        state.ClearAndRetire("session-a");

        Assert.Multiple(() =>
        {
            Assert.That(state.GetActiveSessionId(), Is.Empty);
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(3, 2, "a-2"), 0, replaceOtherSession: true), Is.False);
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(4, 1, "a-3"), 0, replaceOtherSession: true), Is.True);
        });
    }

    [Test]
    public void ClearAndRetire_UsesTheStopGenerationWhenTheStartWasNotAdoptedYet()
    {
        var state = new ServiceClientManager.LogicalSessionState(string.Empty);

        state.ClearAndRetire("session-a", stoppedGeneration: 5);

        Assert.Multiple(() =>
        {
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(5, 1, "a-1"), 0, replaceOtherSession: true), Is.False);
            Assert.That(state.TryAdoptRuntime("session-a", new RuntimeIdentity(6, 1, "a-2"), 0, replaceOtherSession: true), Is.True);
        });
    }

    [Test]
    public async Task HandleUnexpectedRuntimeExitAsync_ClearsStateAndSidecarsOfTheExitedRuntime()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState(string.Empty);
        logicalSession.SetActiveRuntime("session-1", 7, 3, "runtime-1");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 3, "runtime-1");
        var cleaned = new List<string>();

        await ServiceClientManager.HandleUnexpectedRuntimeExitAsync(
            "session-1",
            new RuntimeIdentity(7, 3, "runtime-1"),
            logicalSession,
            runtimeIdentities,
            (sessionId, _) =>
            {
                cleaned.Add(sessionId);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.Empty);
            Assert.That(runtimeIdentities.TryGet("session-1", out _), Is.False);
            Assert.That(cleaned, Is.EqualTo(new[] { "session-1" }));
        });
    }

    [Test]
    public async Task HandleUnexpectedRuntimeExitAsync_ClearsAStartThatUsedAHyphenatedInstanceId()
    {
        var instance = Guid.NewGuid();
        var logicalSession = new ServiceClientManager.LogicalSessionState(string.Empty);
        // The start command carries the hyphenated spelling; the process manager keeps "n".
        logicalSession.SetActiveRuntime("session-1", 7, 3, instance.ToString("D").ToUpperInvariant());
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 3, instance.ToString("D"));
        var cleaned = new List<string>();

        await ServiceClientManager.HandleUnexpectedRuntimeExitAsync(
            "session-1",
            new RuntimeIdentity(7, 3, instance.ToString("n")),
            logicalSession,
            runtimeIdentities,
            (sessionId, _) =>
            {
                cleaned.Add(sessionId);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.Empty);
            Assert.That(runtimeIdentities.TryGet("session-1", out _), Is.False);
            Assert.That(cleaned, Is.EqualTo(new[] { "session-1" }));
        });
    }

    [Test]
    public async Task HandleUnexpectedRuntimeExitAsync_LeavesAReplacementRuntimeAndItsSidecars()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState(string.Empty);
        logicalSession.SetActiveRuntime("session-1", 7, 4, "runtime-2");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 4, "runtime-2");
        var cleaned = new List<string>();

        await ServiceClientManager.HandleUnexpectedRuntimeExitAsync(
            "session-1",
            new RuntimeIdentity(7, 3, "runtime-1"),
            logicalSession,
            runtimeIdentities,
            (sessionId, _) =>
            {
                cleaned.Add(sessionId);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.EqualTo("session-1"));
            Assert.That(runtimeIdentities.TryGet("session-1", out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(new RuntimeIdentity(7, 4, "runtime-2")));
            Assert.That(cleaned, Is.Empty);
        });
    }

    [Test]
    public async Task HandleUnexpectedRuntimeExitAsync_LeavesAReplacementKnownOnlyToTheRegistry()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState(string.Empty);
        logicalSession.SetActiveRuntime("session-1", 7, 3, "runtime-1");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 8, 1, "runtime-2");
        var cleaned = new List<string>();

        await ServiceClientManager.HandleUnexpectedRuntimeExitAsync(
            "session-1",
            new RuntimeIdentity(7, 3, "runtime-1"),
            logicalSession,
            runtimeIdentities,
            (sessionId, _) =>
            {
                cleaned.Add(sessionId);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(runtimeIdentities.TryGet("session-1", out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(new RuntimeIdentity(8, 1, "runtime-2")));
            Assert.That(cleaned, Is.Empty);
        });
    }

    [Test]
    public async Task HandleUnexpectedRuntimeExitAsync_ClearsBySessionWhenTheRuntimeHadNoIdentity()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState("session-1");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        var cleaned = new List<string>();

        await ServiceClientManager.HandleUnexpectedRuntimeExitAsync(
            "session-1",
            default,
            logicalSession,
            runtimeIdentities,
            (sessionId, _) =>
            {
                cleaned.Add(sessionId);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.Empty);
            Assert.That(cleaned, Is.EqualTo(new[] { "session-1" }));
        });
    }

    [Test]
    public void ClearRuntimeSessionStateIfIdentityMatches_PreservesReplacementLaunchForSameSession()
    {
        var logicalSession = new ServiceClientManager.LogicalSessionState(string.Empty);
        logicalSession.SetActiveRuntime("session-1", 7, 4, "runtime-2");
        var runtimeIdentities = new RuntimeIdentityRegistry();
        runtimeIdentities.Set("session-1", 7, 4, "runtime-2");

        ServiceClientManager.ClearRuntimeSessionStateIfIdentityMatches(
            logicalSession,
            runtimeIdentities,
            "session-1",
            7,
            3,
            "runtime-1");

        Assert.Multiple(() =>
        {
            Assert.That(logicalSession.GetActiveSessionId(), Is.EqualTo("session-1"));
            Assert.That(runtimeIdentities.TryGet("session-1", out var identity), Is.True);
            Assert.That(identity, Is.EqualTo(new RuntimeIdentity(7, 4, "runtime-2")));
        });
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_RecordsNotExecutedWhenPreparationIsCancelledBeforeNativeInvocation()
    {
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{\"client_id\":\"client-1\"}"
        };
        Offloadr.Runner.V1.AcknowledgePromptResultRequest? capturedAck = null;
        var capturedAckCancellationToken = CancellationToken.None;
        string? activeDownloadSession = null;
        var submitCalls = 0;
        var registered = 0;
        var unregistered = 0;

        using var cts = new CancellationTokenSource();

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (ack, token) =>
            {
                capturedAck = ack;
                capturedAckCancellationToken = token;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            WithPromptIdentity(prompt),
            PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => registered++,
            value => activeDownloadSession = value,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask,
            (_, _) => { },
            (_, token, _) => { cts.Cancel(); return Task.FromCanceled(token); },
            (_, _, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{\"prompt_id\":\"p1\"}"), "Prompt submitted"));
            },
            (_, _, _, _) => { },
                        cts.Token,
            unregisterUninvokedSubmission: (_, _, _) => unregistered++);

        Assert.That(activeDownloadSession, Is.EqualTo(PromptSessionId));
        Assert.That(submitCalls, Is.EqualTo(0));
        Assert.That(registered, Is.EqualTo(1));
        Assert.That(unregistered, Is.EqualTo(1));
        Assert.That(capturedAck, Is.Not.Null);
        Assert.That(capturedAck!.Result.Outcome, Is.EqualTo(Offloadr.Common.V1.PromptInvocationOutcome.NotExecuted));
        Assert.That(capturedAck.Result.NativeResponse, Is.Null);
        Assert.That(capturedAckCancellationToken.IsCancellationRequested, Is.False);
        Assert.That(capturedAckCancellationToken.CanBeCanceled, Is.False);
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_LostPermissionResponseCannotInvokeRuntimeOrInventResult()
    {
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{}"
        };

        Offloadr.Runner.V1.AcknowledgePromptResultRequest? capturedAck = null;
        var grantCalls = 0;
        using var cts = new CancellationTokenSource();

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (ack, _) =>
            {
                capturedAck = ack;
                return Task.CompletedTask;
            },
            (_, _) => Task.CompletedTask,
            WithPromptIdentity(prompt),
            PromptRunnerId,
            (_, _) =>
            {
                grantCalls++;
                cts.Cancel();
                throw new IOException("grant response lost");
            },
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => { },
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask,
            (_, _) => { },
            (_, token, _) => Task.FromCanceled(token),
            (_, _, _, _) => throw new AssertionException("No permission response can authorize native execution"),
            (_, _, _, _) => { },
                        cts.Token);

        Assert.That(capturedAck, Is.Null);
        Assert.That(grantCalls, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task HandleSubmitEditorActionAsync_RetriesSameGrantAndNeverRepeatsNativeAfterLostCommittedResponse(bool grantWasCommitted)
    {
        var prompt = WithPromptIdentity(new SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{}"
        });
        var grants = 0;
        var recoveries = 0;
        var nativeCalls = 0;
        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            prompt,
            PromptRunnerId,
            (request, token) =>
            {
                grants++;
                if (grants == 1) throw new RpcException(new(StatusCode.Unavailable, "response lost"));
                if (!grantWasCommitted) return GrantPromptForTestAsync(request, token);
                return Task.FromResult(new GrantPromptExecutionResponse
                {
                    MayExecute = false,
                    Submission = new()
                    {
                        SubmissionId = request.SubmissionId,
                        Target = request.Target.Clone(),
                        ExecutionPermissionUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTime(DateTime.UtcNow)
                    }
                });
            },
            (_, _) => Task.CompletedTask,
            (_, _) => { recoveries++; return Task.CompletedTask; },
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => { },
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask,
            (_, _) => { },
            (_, _, _) => Task.CompletedTask,
            (_, _, _, _) =>
            {
                nativeCalls++;
                return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, "{}"u8.ToArray(), "Prompt submitted"));
            },
            (_, _, _, _) => { },
            CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(grants, Is.EqualTo(2));
            Assert.That(recoveries, Is.EqualTo(grantWasCommitted ? 1 : 0));
            Assert.That(nativeCalls, Is.EqualTo(grantWasCommitted ? 0 : 1));
        });
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_ProjectsLocalModelsAndSkipsDownloadCoordinator()
    {
        var localDownload = new Offloadr.Runner.V1.ModelDownloadRequest
        {
            ModelId = "local-model",
            Filename = "local.safetensors",
            DestinationPath = "/models/local.safetensors",
            LocalSelectionHash = new string('a', Offloadr.Common.V1.LocalModelSelectionHash.Sha256HexLength)
        };
        var remoteDownload = new Offloadr.Runner.V1.ModelDownloadRequest
        {
            ModelId = "remote-model",
            Filename = "remote.safetensors",
            DestinationPath = "/models/remote.safetensors",
            SourceUrl = "https://models.example.test/remote.safetensors"
        };
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{}"
        };
        prompt.Downloads.Add(localDownload);
        prompt.Downloads.Add(remoteDownload);

        IReadOnlyList<Offloadr.Runner.V1.ModelDownloadRequest>? projectedDownloads = null;
        bool? replaceExisting = null;
        IReadOnlyList<Offloadr.Runner.V1.ModelDownloadRequest>? registeredDownloads = null;
        IReadOnlyList<Offloadr.Runner.V1.ModelDownloadRequest>? ensuredDownloads = null;

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            WithPromptIdentity(prompt),
            PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => { },
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask,
            (_, downloads) => registeredDownloads = downloads.ToArray(),
            (downloads, _, _) =>
            {
                ensuredDownloads = downloads.ToArray();
                return Task.CompletedTask;
            },
            (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{}"), "Prompt submitted")),
            (_, _, _, _) => { },
                        CancellationToken.None,
            (downloads, replace) =>
            {
                projectedDownloads = downloads.ToArray();
                replaceExisting = replace;
            });

        Assert.That(projectedDownloads, Is.Not.Null);
        Assert.That(projectedDownloads!, Has.Count.EqualTo(2));
        Assert.That(replaceExisting, Is.False);
        Assert.That(registeredDownloads, Is.Not.Null);
        Assert.That(registeredDownloads!, Is.EqualTo(new[] { remoteDownload }));
        Assert.That(ensuredDownloads, Is.Not.Null);
        Assert.That(ensuredDownloads!, Is.EqualTo(new[] { remoteDownload }));
    }

    [Test]
    public void MaterializeModelDownloadPathsForSession_WhenForgeNeo_UsesRelativeModelPath()
    {
        var download = new Offloadr.Runner.V1.ModelDownloadRequest
        {
            Filename = "model.safetensors",
            RelativeModelPath = "Stable-diffusion/model.safetensors"
        };

        var result = ServiceClientManager.MaterializeModelDownloadPathsForSession(
            [download],
            "forge-neo",
            new SessionProcessManager.SessionPaths { ModelsDirectory = "/sessions/session-1/models" });

        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].DestinationPath, Is.EqualTo("/sessions/session-1/models/Stable-diffusion/model.safetensors"));
        Assert.That(result[0].RelativeModelPath, Is.EqualTo("Stable-diffusion/model.safetensors"));
        Assert.That(download.DestinationPath, Is.Empty);
    }

    [Test]
    public void MaterializeModelDownloadPathsForSession_WhenForgeNeoRelativePathEscapesSessionRoot_Throws()
    {
        var download = new Offloadr.Runner.V1.ModelDownloadRequest
        {
            Filename = "model.safetensors",
            RelativeModelPath = "../model.safetensors"
        };

        Assert.That(
            () => ServiceClientManager.MaterializeModelDownloadPathsForSession(
                [download],
                "forge-neo",
                new SessionProcessManager.SessionPaths { ModelsDirectory = "/sessions/session-1/models" }),
            Throws.InvalidOperationException);
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_AcksSuccess_AndMapsPromptId()
    {
        var originalDelays = ServiceClientManager.PromptArtifactRefreshDelays;
        ServiceClientManager.PromptArtifactRefreshDelays = [TimeSpan.Zero];
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{\"client_id\":\"client-1\"}"
        };
        Offloadr.Runner.V1.AcknowledgePromptResultRequest? capturedAck = null;
        string? registeredSubmissionClient = null;
        string? mappedPromptId = null;
        string? mappedSubmissionId = null;
        string? activeDownloadSession = null;
        var refreshCalls = 0;

        try
        {
            await ServiceClientManager.HandleSubmitEditorActionAsync(
                (ack, _) =>
                {
                    capturedAck = ack;
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                WithPromptIdentity(prompt),
                PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, clientId, _) => registeredSubmissionClient = clientId,
                value => activeDownloadSession = value,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _, _, _) => Task.CompletedTask,
                _ => () =>
                {
                    Interlocked.Increment(ref refreshCalls);
                    return Task.CompletedTask;
                },
                (_, _) => { },
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{\"prompt_id\":\"prompt-1\"}"), "Prompt submitted")),
                (_, _, promptId, submissionId) =>
                {
                    mappedPromptId = promptId;
                    mappedSubmissionId = submissionId;
                },
                            CancellationToken.None);

            await Task.Delay(100).ConfigureAwait(false);

            Assert.That(activeDownloadSession, Is.EqualTo(PromptSessionId));
            Assert.That(registeredSubmissionClient, Is.EqualTo("client-1"));
            Assert.That(mappedPromptId, Is.EqualTo("prompt-1"));
            Assert.That(mappedSubmissionId, Is.EqualTo(PromptSubmissionId));
            Assert.That(refreshCalls, Is.GreaterThanOrEqualTo(1));
            Assert.That(capturedAck, Is.Not.Null);
            Assert.That(capturedAck!.Result.Outcome, Is.EqualTo(Offloadr.Common.V1.PromptInvocationOutcome.NativeResponse));
            Assert.That(capturedAck.Result.NativeResponse.StatusCode, Is.EqualTo(200));
        }
        finally
        {
            ServiceClientManager.PromptArtifactRefreshDelays = originalDelays;
        }
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_ForForgeQueueData_ReportsStreamedHttpChunks()
    {
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            EditorRuntimeKind = "forge-neo",
            HttpMethod = "GET",
            HttpPath = "queue/data",
            PromptJson = "{}"
        };

        Offloadr.Runner.V1.ReportSessionEventsRequest? capturedEvents = null;
        var sinkWasProvided = false;

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (_, _) => Task.CompletedTask,
            (events, _) =>
            {
                capturedEvents = events;
                return Task.CompletedTask;
            },
            WithPromptIdentity(prompt),
            PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => { },
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask,
            (_, _) => { },
            (_, _, _) => Task.CompletedTask,
            async (_, responseEventSink, _, token) =>
            {
                sinkWasProvided = responseEventSink is not null;
                Assert.That(responseEventSink, Is.Not.Null);

                var payload = System.Text.Encoding.UTF8.GetBytes("data: ping\n\n");
                await responseEventSink!(ServiceClientManager.HttpResponseChunkEventType, payload, token).ConfigureAwait(false);
                return new SessionProcessManager.PromptSubmissionResult(true, 200, [], "Prompt submitted", "text/event-stream");
            },
            (_, _, _, _) => { },
                        CancellationToken.None);

        Assert.That(sinkWasProvided, Is.True);
        Assert.That(capturedEvents, Is.Not.Null);
        var streamEvent = capturedEvents!.Events.Single();
        Assert.That(streamEvent.RunnerId, Is.EqualTo(PromptRunnerId));
        Assert.That(streamEvent.SessionId, Is.EqualTo(PromptSessionId));
        Assert.That(streamEvent.EditorSid, Is.EqualTo("editor-1"));
        Assert.That(streamEvent.ClientId, Is.EqualTo($"http-relay:{PromptSubmissionId}"));
        Assert.That(streamEvent.SubmissionId, Is.EqualTo(PromptSubmissionId));
        Assert.That(streamEvent.EventType, Is.EqualTo("http.response.chunk"));
        Assert.That(streamEvent.FrameType, Is.EqualTo(Offloadr.Runner.V1.SessionStreamFrameType.Binary));
        Assert.That(streamEvent.Payload.ToStringUtf8(), Is.EqualTo("data: ping\n\n"));
    }

    [Test]
    public async Task ForgeTerminalEvidenceSurvivesLiveFanoutFailure()
    {
        var prompt = WithPromptIdentity(new()
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            EditorRuntimeKind = "forge-neo",
            HttpMethod = "GET",
            HttpPath = "queue/data",
            PromptJson = "{}",
        });
        var retained = new TaskCompletionSource<ReportPromptEvidenceRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        AcknowledgePromptResultRequest? original = null;
        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (ack, _) => { original = ack; return Task.CompletedTask; },
            (_, _) => throw new IOException("stream fanout unavailable"),
            prompt, PromptRunnerId, GrantPromptForTestAsync,
            (evidence, _) => { retained.TrySetResult(evidence); return Task.CompletedTask; },
            (_, _) => throw new AssertionException("Unexpected recovery"),
            (_, _, _, _) => Task.CompletedTask, (_, _, _) => { }, _ => { },
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask, (_, _) => { }, (_, _, _) => Task.CompletedTask,
            async (_, stream, _, token) =>
            {
                await stream!(ServiceClientManager.HttpResponseChunkEventType,
                    System.Text.Encoding.UTF8.GetBytes("data: {\"msg\":\"process_completed\",\"event_id\":\"event-1\",\"success\":true}\n\n"), token);
                return new SessionProcessManager.PromptSubmissionResult(true, 200, [], "observed", "text/event-stream");
            }, (_, _, _, _) => { }, CancellationToken.None);
        var reported = await retained.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(reported.Target, Is.EqualTo(prompt.Target));
        Assert.That(reported.CommandId, Is.EqualTo(prompt.CommandId));
        Assert.That(reported.Evidence.NativeIdentifiers.SessionHash, Is.EqualTo(prompt.NativeIdentifiers.SessionHash));
        Assert.That(original!.Result.Outcome, Is.EqualTo(Offloadr.Common.V1.PromptInvocationOutcome.NativeResponse));
    }

    [Test]
    public async Task ForgeTerminalEvidenceRpcCannotBlockNativeLiveChunkOrStop()
    {
        var prompt = WithPromptIdentity(new()
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            EditorRuntimeKind = "forge-neo",
            HttpMethod = "GET",
            HttpPath = "queue/data",
            PromptJson = "{}",
        });
        var evidenceStarted = new TaskCompletionSource<ReportPromptEvidenceRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEvidence = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evidenceDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var liveChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var physical = new CancellationTokenSource();
        var nativeCalls = 0;
        AcknowledgePromptResultRequest? acknowledgement = null;
        var handling = ServiceClientManager.HandleSubmitEditorActionAsync(
            (result, _) => { acknowledgement = result; return Task.CompletedTask; },
            (_, _) => { liveChunk.TrySetResult(); return Task.CompletedTask; },
            prompt, PromptRunnerId, GrantPromptForTestAsync,
            async (evidence, _) =>
            {
                evidenceStarted.TrySetResult(evidence);
                await releaseEvidence.Task.ConfigureAwait(false);
                evidenceDelivered.TrySetResult();
            },
            (_, _) => throw new AssertionException("Unexpected recovery"),
            (_, _, _, _) => Task.CompletedTask, (_, _, _) => { }, _ => { },
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _, _) => Task.CompletedTask,
            _ => () => Task.CompletedTask, (_, _) => { }, (_, _, _) => Task.CompletedTask,
            async (_, stream, onNativeRequestAttempt, token) =>
            {
                onNativeRequestAttempt();
                nativeCalls++;
                await stream!(ServiceClientManager.HttpResponseChunkEventType,
                    System.Text.Encoding.UTF8.GetBytes("data: {\"msg\":\"process_completed\",\"event_id\":\"event-1\",\"success\":true}\n\n"), token);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                throw new AssertionException("The cancelled native reader must not complete successfully.");
            }, (_, _, _, _) => { }, physical.Token);
        var captured = await evidenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await liveChunk.Task.WaitAsync(TimeSpan.FromSeconds(3));
        physical.Cancel();
        await handling.WaitAsync(TimeSpan.FromSeconds(3));
        releaseEvidence.TrySetResult();
        await evidenceDelivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.That(captured.Evidence.NativeIdentifiers.SessionHash, Is.EqualTo(prompt.NativeIdentifiers.SessionHash));
        Assert.That(nativeCalls, Is.EqualTo(1));
        Assert.That(acknowledgement?.Result.Outcome, Is.EqualTo(Offloadr.Common.V1.PromptInvocationOutcome.Unknown));
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_SeedsPromptArtifactReferences()
    {
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            Owner = "alice",
            EditorRuntimeKind = "forge-neo",
            HttpMethod = "POST",
            HttpPath = "queue/join",
            PromptJson = "{}"
        };
        prompt.ArtifactReferences.Add(new Offloadr.Common.V1.PromptArtifactReference
        {
            Placeholder = "offloadr://forge-artifact/submission-1/0",
            Filename = "0000-input.png",
            Type = "temp",
            Subfolder = "forge-queue/submission-1"
        });

        IReadOnlyList<Offloadr.EditorRuntime.V1.EditorArtifactMetadata>? seededArtifacts = null;

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            WithPromptIdentity(prompt),
            PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => { },
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _, artifacts, _) =>
            {
                seededArtifacts = artifacts.ToArray();
                return Task.CompletedTask;
            },
            _ => () => Task.CompletedTask,
            (_, _) => { },
            (_, _, _) => Task.CompletedTask,
            (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{}"), "Prompt submitted")),
            (_, _, _, _) => { },
                        CancellationToken.None);

        Assert.That(seededArtifacts, Is.Not.Null);
        Assert.That(seededArtifacts!, Has.Count.EqualTo(1));
        Assert.That(seededArtifacts![0].EditorSid, Is.EqualTo("editor-1"));
        Assert.That(seededArtifacts![0].Filename, Is.EqualTo("0000-input.png"));
        Assert.That(seededArtifacts![0].Type, Is.EqualTo("temp"));
        Assert.That(seededArtifacts![0].Subfolder, Is.EqualTo("forge-queue/submission-1"));
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_RefreshesArtifactsBeforeSubmittingPrompt()
    {
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            Owner = "alice",
            EditorRuntimeKind = "comfyui",
            HttpMethod = "POST",
            HttpPath = "api/prompt",
            PromptJson = "{}"
        };
        var order = new List<string>();

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            WithPromptIdentity(prompt),
            PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => { },
            (_, _, _, _) =>
            {
                order.Add("activate");
                return Task.CompletedTask;
            },
            (_, _, _, _, _) => Task.CompletedTask,
            _ => () =>
            {
                order.Add("refresh");
                return Task.CompletedTask;
            },
            (_, _) => { },
            (_, _, _) => Task.CompletedTask,
            (_, _, _, _) =>
            {
                order.Add("submit");
                return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(false, 500, System.Text.Encoding.UTF8.GetBytes("{}"), "Prompt rejected"));
            },
            (_, _, _, _) => { },
                        CancellationToken.None);

        Assert.That(order, Is.EqualTo(new[] { "activate", "refresh", "submit" }));
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_SkipsGenerationPreparation_ForComfyControlDefenseInDepth()
    {
        var prompt = new SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            Owner = "alice",
            EditorRuntimeKind = "comfyui",
            HttpMethod = "POST",
            HttpPath = "api/interrupt",
            PromptJson = "{}"
        };
        var preparationCalls = 0;
        var submitCalls = 0;

        await ServiceClientManager.HandleSubmitEditorActionAsync(
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            WithPromptIdentity(prompt),
            PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
            (_, _, _, _) => Task.CompletedTask,
            (_, _, _) => { },
            _ => preparationCalls++,
            (_, _, _, _) => { preparationCalls++; return Task.CompletedTask; },
            (_, _, _, _, _) => { preparationCalls++; return Task.CompletedTask; },
            _ => () => { preparationCalls++; return Task.CompletedTask; },
            (_, _) => preparationCalls++,
            (_, _, _) => { preparationCalls++; return Task.CompletedTask; },
            (_, _, _, _) =>
            {
                submitCalls++;
                return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{}"), "relayed"));
            },
            (_, _, _, _) => { },
                        CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(preparationCalls, Is.EqualTo(0));
            Assert.That(submitCalls, Is.EqualTo(1));
        });
    }

    [Test]
    public void PrepareForgePromptJson_RewritesArtifactPlaceholdersToSessionTempPaths()
    {
        LinuxTestPrerequisites.RequireLinux();

        var paths = new SessionProcessManager.SessionPaths
        {
            TempDirectory = "/tmp/offloadr-session/temp",
            OutputDirectory = "/tmp/offloadr-session/output"
        };
        var body = """{"data":[{"path":"offloadr://forge-artifact/submission-1/0","url":"offloadr://forge-artifact/submission-1/0"}]}""";
        var references = new[]
        {
            new Offloadr.Common.V1.PromptArtifactReference
            {
                Placeholder = "offloadr://forge-artifact/submission-1/0",
                Filename = "0000-input.png",
                Type = "temp",
                Subfolder = "forge-queue/submission-1"
            }
        };

        var rewritten = SessionProcessManager.PrepareForgePromptJson(body, references, paths);

        Assert.That(rewritten, Does.Contain("/tmp/offloadr-session/temp/forge-queue/submission-1/0000-input.png"));
        Assert.That(rewritten, Does.Not.Contain("offloadr://forge-artifact"));
    }

    [Test]
    public void PrepareForgePromptJson_DoesNotCascadePrefixArtifactPlaceholders()
    {
        LinuxTestPrerequisites.RequireLinux();

        var tempRoot = Path.Combine(Path.GetTempPath(), $"forge-placeholder-{Guid.NewGuid():N}");
        try
        {
            var paths = new SessionProcessManager.SessionPaths
            {
                TempDirectory = Path.Combine(tempRoot, "temp"),
                OutputDirectory = Path.Combine(tempRoot, "output")
            };
            var body = """
                {
                  "data": [
                    {"path": "offloadr://forge-artifact/submission-1/1"},
                    {"path": "offloadr://forge-artifact/submission-1/10"}
                  ]
                }
                """;
            var references = new[]
            {
                new Offloadr.Common.V1.PromptArtifactReference
                {
                    Placeholder = "offloadr://forge-artifact/submission-1/1",
                    Filename = "one.png",
                    Type = "temp",
                    Subfolder = "forge-queue/submission-1"
                },
                new Offloadr.Common.V1.PromptArtifactReference
                {
                    Placeholder = "offloadr://forge-artifact/submission-1/10",
                    Filename = "ten.png",
                    Type = "temp",
                    Subfolder = "forge-queue/submission-1"
                }
            };

            var rewritten = SessionProcessManager.PrepareForgePromptJson(body, references, paths);
            using var doc = System.Text.Json.JsonDocument.Parse(rewritten);
            var data = doc.RootElement.GetProperty("data");

            Assert.That(
                data[0].GetProperty("path").GetString(),
                Is.EqualTo(Path.Combine(paths.TempDirectory, "forge-queue", "submission-1", "one.png")));
            Assert.That(
                data[1].GetProperty("path").GetString(),
                Is.EqualTo(Path.Combine(paths.TempDirectory, "forge-queue", "submission-1", "ten.png")));
            Assert.That(rewritten, Does.Not.Contain("offloadr://forge-artifact"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Test]
    public void NormalizeForgeQueueJoinDataArity_AppendsNullsWhenRunnerConfigExpectsMoreInputs()
    {
        var body = """{"fn_index":234,"session_hash":"abc","data":["task(1)","prompt"]}""";
        var config = BuildForgeConfigWithInputCount(fnIndex: 234, inputCount: 4);

        var normalized = SessionProcessManager.NormalizeForgeQueueJoinDataArity(body, config, out var adjustment);
        using var doc = System.Text.Json.JsonDocument.Parse(normalized);
        var data = doc.RootElement.GetProperty("data");

        Assert.Multiple(() =>
        {
            Assert.That(adjustment, Is.Not.Null);
            Assert.That(adjustment!.FnIndex, Is.EqualTo(234));
            Assert.That(adjustment.ReceivedCount, Is.EqualTo(2));
            Assert.That(adjustment.ExpectedCount, Is.EqualTo(4));
            Assert.That(data.GetArrayLength(), Is.EqualTo(4));
            Assert.That(data[0].GetString(), Is.EqualTo("task(1)"));
            Assert.That(data[1].GetString(), Is.EqualTo("prompt"));
            Assert.That(data[2].ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
            Assert.That(data[3].ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
        });
    }

    [Test]
    public void NormalizeForgeQueueJoinDataArity_AppendsNullsWhenDependencyIdIsSparse()
    {
        var body = """{"fn_index":234,"session_hash":"abc","data":["task(1)","prompt"]}""";
        const string config = """
            {
              "dependencies": [
                { "id": 96, "inputs": [1] },
                { "id": 234, "inputs": [1,2,3,4] }
              ]
            }
            """;

        var normalized = SessionProcessManager.NormalizeForgeQueueJoinDataArity(body, config, out var adjustment);
        using var doc = System.Text.Json.JsonDocument.Parse(normalized);
        var data = doc.RootElement.GetProperty("data");

        Assert.Multiple(() =>
        {
            Assert.That(adjustment, Is.Not.Null);
            Assert.That(adjustment!.FnIndex, Is.EqualTo(234));
            Assert.That(adjustment.ReceivedCount, Is.EqualTo(2));
            Assert.That(adjustment.ExpectedCount, Is.EqualTo(4));
            Assert.That(data.GetArrayLength(), Is.EqualTo(4));
            Assert.That(data[2].ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
            Assert.That(data[3].ValueKind, Is.EqualTo(System.Text.Json.JsonValueKind.Null));
        });
    }

    [Test]
    public void NormalizeForgeQueueJoinDataArity_LeavesPayloadWhenEditorHasEnoughInputs()
    {
        var body = """{"fn_index":"3","session_hash":"abc","data":[1,2,3]}""";
        var config = BuildForgeConfigWithInputCount(fnIndex: 3, inputCount: 2);

        var normalized = SessionProcessManager.NormalizeForgeQueueJoinDataArity(body, config, out var adjustment);

        Assert.Multiple(() =>
        {
            Assert.That(adjustment, Is.Null);
            Assert.That(normalized, Is.EqualTo(body));
        });
    }

    [Test]
    public void BuildForgeProgressRequestJson_UsesForgeIdLivePreviewShape()
    {
        var body = SessionProcessManager.BuildForgeProgressRequestJson("task(progress-1)");

        Assert.Multiple(() =>
        {
            Assert.That(body, Does.Contain("\"id_task\":\"task(progress-1)\""));
            Assert.That(body, Does.Contain("\"id_live_preview\":-1"));
            Assert.That(body, Does.Not.Contain("\"live_preview\""));
        });
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_RunsDelayedArtifactRefreshSweeps()
    {
        var originalDelays = ServiceClientManager.PromptArtifactRefreshDelays;
        ServiceClientManager.PromptArtifactRefreshDelays =
        [
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(25),
            TimeSpan.FromMilliseconds(50)
        ];

        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{\"client_id\":\"client-1\"}"
        };

        var refreshCalls = 0;

        try
        {
            await ServiceClientManager.HandleSubmitEditorActionAsync(
                (ack, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                WithPromptIdentity(prompt),
                PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => { },
                _ => { },
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _, _, _) => Task.CompletedTask,
                _ => () =>
                {
                    Interlocked.Increment(ref refreshCalls);
                    return Task.CompletedTask;
                },
                (_, _) => { },
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{\"prompt_id\":\"prompt-1\"}"), "Prompt submitted")),
                (_, _, _, _) => { },
                            CancellationToken.None);

            await Task.Delay(200).ConfigureAwait(false);

            Assert.That(refreshCalls, Is.EqualTo(3));
        }
        finally
        {
            ServiceClientManager.PromptArtifactRefreshDelays = originalDelays;
        }
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_DoesNotRefreshArtifacts_ForForgeProgressRelay()
    {
        var originalDelays = ServiceClientManager.PromptArtifactRefreshDelays;
        ServiceClientManager.PromptArtifactRefreshDelays = [TimeSpan.Zero];

        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            Owner = "alice",
            EditorRuntimeKind = "forge-neo",
            HttpMethod = "POST",
            HttpPath = "internal/progress",
            PromptJson = """{"id_task":"task(progress-1)","id_live_preview":-1}"""
        };

        var refreshCalls = 0;

        try
        {
            await ServiceClientManager.HandleSubmitEditorActionAsync(
                (ack, _) => Task.CompletedTask,
                (_, _) => Task.CompletedTask,
                WithPromptIdentity(prompt),
                PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => { },
                _ => { },
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _, _, _) => Task.CompletedTask,
                _ => () =>
                {
                    Interlocked.Increment(ref refreshCalls);
                    return Task.CompletedTask;
                },
                (_, _) => { },
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(
                    true,
                    200,
                    System.Text.Encoding.UTF8.GetBytes("""{"active":true,"progress":0.5}"""),
                    "Prompt submitted",
                    "application/json")),
                (_, _, _, _) => { },
                            CancellationToken.None);

            await Task.Delay(50).ConfigureAwait(false);

            Assert.That(refreshCalls, Is.EqualTo(0));
        }
        finally
        {
            ServiceClientManager.PromptArtifactRefreshDelays = originalDelays;
        }
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_KeepsSuccessfulAck_WhenArtifactRefreshFails()
    {
        var originalDelays = ServiceClientManager.PromptArtifactRefreshDelays;
        ServiceClientManager.PromptArtifactRefreshDelays = [TimeSpan.Zero];
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{\"client_id\":\"client-1\"}"
        };
        Offloadr.Runner.V1.AcknowledgePromptResultRequest? capturedAck = null;

        try
        {
            await ServiceClientManager.HandleSubmitEditorActionAsync(
                (ack, _) =>
                {
                    capturedAck = ack;
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                WithPromptIdentity(prompt),
                PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => { },
                _ => { },
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _, _, _) => Task.CompletedTask,
                _ => () => throw new InvalidOperationException("refresh failed"),
                (_, _) => { },
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{\"prompt_id\":\"prompt-1\"}"), "Prompt submitted")),
                (_, _, _, _) => { },
                            CancellationToken.None);

            Assert.That(capturedAck, Is.Not.Null);
            Assert.That(capturedAck!.Result.Outcome, Is.EqualTo(Offloadr.Common.V1.PromptInvocationOutcome.NativeResponse));
            Assert.That(capturedAck.Result.NativeResponse.StatusCode, Is.EqualTo(200));
        }
        finally
        {
            ServiceClientManager.PromptArtifactRefreshDelays = originalDelays;
        }
    }

    [Test]
    public async Task HandleSubmitEditorActionAsync_DoesNotWaitForArtifactRefreshBeforeAck()
    {
        var originalDelays = ServiceClientManager.PromptArtifactRefreshDelays;
        ServiceClientManager.PromptArtifactRefreshDelays = [TimeSpan.Zero];
        var prompt = new Offloadr.Runner.V1.SubmitPromptCommand
        {
            SessionId = PromptSessionId,
            SubmissionId = PromptSubmissionId,
            EditorSid = "editor-1",
            PromptJson = "{\"client_id\":\"client-1\"}"
        };
        Offloadr.Runner.V1.AcknowledgePromptResultRequest? capturedAck = null;
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var handleTask = ServiceClientManager.HandleSubmitEditorActionAsync(
                (ack, _) =>
                {
                    capturedAck = ack;
                    return Task.CompletedTask;
                },
                (_, _) => Task.CompletedTask,
                WithPromptIdentity(prompt),
                PromptRunnerId,
            GrantPromptForTestAsync,
            (_, _) => Task.CompletedTask,
            (_, _) => Task.CompletedTask,
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => { },
                _ => { },
                (_, _, _, _) => Task.CompletedTask,
                (_, _, _, _, _) => Task.CompletedTask,
                _ => async () =>
                {
                    refreshStarted.TrySetResult();
                    await releaseRefresh.Task.ConfigureAwait(false);
                },
                (_, _) => { },
                (_, _, _) => Task.CompletedTask,
                (_, _, _, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, System.Text.Encoding.UTF8.GetBytes("{\"prompt_id\":\"prompt-1\"}"), "Prompt submitted")),
                (_, _, _, _) => { },
                            CancellationToken.None);

            var completed = await Task.WhenAny(handleTask, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            Assert.That(completed, Is.SameAs(handleTask), "prompt handling should not wait for artifact refresh");

            Assert.That(capturedAck, Is.Not.Null);
            Assert.That(capturedAck!.Result.Outcome, Is.EqualTo(Offloadr.Common.V1.PromptInvocationOutcome.NativeResponse));
            Assert.That(capturedAck.Result.NativeResponse.StatusCode, Is.EqualTo(200));

            await refreshStarted.Task.ConfigureAwait(false);
            releaseRefresh.TrySetResult();
            await handleTask.ConfigureAwait(false);
        }
        finally
        {
            releaseRefresh.TrySetResult();
            ServiceClientManager.PromptArtifactRefreshDelays = originalDelays;
        }
    }

    private static LocalModelSnapshot CreateSnapshot(string digest, params string[] selectionHashes)
    {
        var normalizedHashes = selectionHashes
            .Select(Offloadr.Common.V1.LocalModelSelectionHash.NormalizeSelectionHash)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        return new LocalModelSnapshot(
            [],
            digest,
            [],
            normalizedHashes,
            Offloadr.Common.V1.LocalModelSelectionHash.ComputeSnapshotDigest(normalizedHashes));
    }

    private static string BuildForgeConfigWithInputCount(int fnIndex, int inputCount)
    {
        var dependencies = Enumerable.Range(0, fnIndex + 1)
            .Select(index => index == fnIndex
                ? $$"""{"inputs":[{{string.Join(",", Enumerable.Range(1, inputCount))}}]}"""
                : """{}""");
        return $$"""{"dependencies":[{{string.Join(",", dependencies)}}]}""";
    }

}

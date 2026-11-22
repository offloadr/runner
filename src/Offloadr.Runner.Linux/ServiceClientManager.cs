using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using Offloadr.Runner.V1;
using Offloadr.Common.V1;
using Offloadr.EditorRuntime.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace Offloadr.Runner.Linux;

internal static partial class ServiceClientManager
{
    internal const string HttpResponseChunkEventType = "http.response.chunk";

    internal sealed class TransientAcknowledgementCache(
        int maxEntries = 256,
        long maxBytes = 64L * 1024 * 1024)
    {
        private sealed record CacheEntry(
            AcknowledgeEditorRuntimeRequestRequest Response,
            ByteString Fingerprint,
            long SizeBytes,
            LinkedListNode<string> InsertionNode);

        private readonly object _gate = new();
        private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _insertionOrder = new();
        private readonly int _maxEntries = maxEntries > 0 ? maxEntries : throw new ArgumentOutOfRangeException(nameof(maxEntries));
        private readonly long _maxBytes = maxBytes > 0 ? maxBytes : throw new ArgumentOutOfRangeException(nameof(maxBytes));
        private long _retainedBytes;

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        public long RetainedBytes
        {
            get { lock (_gate) return _retainedBytes; }
        }

        public void Set(AcknowledgeEditorRuntimeRequestRequest response, ByteString fingerprint)
        {
            var cached = response;
            var sizeBytes = (long)cached.CalculateSize() + fingerprint.Length;
            lock (_gate)
            {
                if (sizeBytes > _maxBytes)
                {
                    return;
                }

                if (_entries.ContainsKey(cached.RequestId))
                {
                    return;
                }

                while (_entries.Count >= _maxEntries || _retainedBytes + sizeBytes > _maxBytes)
                {
                    var oldest = _insertionOrder.First;
                    if (oldest is null)
                    {
                        break;
                    }

                    _insertionOrder.RemoveFirst();
                    if (_entries.Remove(oldest.Value, out var evicted))
                    {
                        _retainedBytes -= evicted.SizeBytes;
                    }
                }

                var insertionNode = _insertionOrder.AddLast(cached.RequestId);
                _entries[cached.RequestId] = new CacheEntry(cached, fingerprint, sizeBytes, insertionNode);
                _retainedBytes += sizeBytes;
            }
        }

        public bool TryGet(string requestId, out AcknowledgeEditorRuntimeRequestRequest response, out ByteString fingerprint)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(requestId, out var cached))
                {
                    response = cached.Response.Clone();
                    fingerprint = cached.Fingerprint;
                    return true;
                }
            }

            response = null!;
            fingerprint = ByteString.Empty;
            return false;
        }

        public void Remove(string requestId)
        {
            lock (_gate)
            {
                if (_entries.Remove(requestId, out var removed))
                {
                    _retainedBytes -= removed.SizeBytes;
                    _insertionOrder.Remove(removed.InsertionNode);
                }
            }
        }
    }

    internal enum TransientRequestAdmission
    {
        Execute,
        Joined,
        Replay,

        /// <summary>The same request ID was used for different immutable command content.</summary>
        IdentityMismatch,

        /// <summary>The request already ran here, but its result is no longer retained.</summary>
        AlreadyExecuted,

        /// <summary>A previously unseen request is addressed to another runner.</summary>
        WrongRunner,

        /// <summary>The request had no deadline, or it passed before the request could run.</summary>
        Expired,

        /// <summary>Too many requests are remembered as executed to admit another safely.</summary>
        Saturated,
    }

    /// <summary>
    /// Tracks deliveries of transient editor requests by request id. Completed
    /// results stay in the bounded acknowledgement cache, so a redelivery replays
    /// the original result instead of executing again; deliveries that arrive while
    /// the request runs are all acknowledged with its result.
    /// </summary>
    internal sealed class TransientRequestDeliveries(TransientAcknowledgementCache completed)
    {
        /// <summary>
        /// Requests remembered as executed until their deadline. A request is never admitted
        /// after its deadline, so remembering it until then is enough to never run it twice.
        /// Markers are never evicted early: at this limit new requests are refused instead.
        /// </summary>
        internal static int MaxExecutedMarkers { get; set; } = 16_384;

        private readonly object _gate = new();
        private sealed record InFlightRequest(ByteString Fingerprint, DateTime DeadlineUtc, List<string> Deliveries);

        private readonly Dictionary<string, InFlightRequest> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _executedUntil = new(StringComparer.Ordinal);
        private readonly PriorityQueue<string, DateTime> _executedByDeadline = new();

        public int InFlightCount
        {
            get { lock (_gate) return _inFlight.Count; }
        }

        public int ExecutedMarkerCount
        {
            get { lock (_gate) return _executedUntil.Count; }
        }

        public TransientRequestAdmission Admit(
            RelayEditorRuntimeRequestCommand request,
            string runnerId,
            out AcknowledgeEditorRuntimeRequestRequest? replay,
            DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            var fingerprint = Fingerprint(request);
            replay = null;
            lock (_gate)
            {
                if (completed.TryGet(request.RequestId, out var cached, out var cachedFingerprint))
                {
                    if (!fingerprint.Equals(cachedFingerprint))
                        return TransientRequestAdmission.IdentityMismatch;

                    cached.DeliveryId = request.DeliveryId;
                    replay = cached;
                    return TransientRequestAdmission.Replay;
                }

                if (_inFlight.TryGetValue(request.RequestId, out var inFlight))
                {
                    if (!fingerprint.Equals(inFlight.Fingerprint))
                        return TransientRequestAdmission.IdentityMismatch;

                    if (!inFlight.Deliveries.Contains(request.DeliveryId, StringComparer.Ordinal))
                    {
                        inFlight.Deliveries.Add(request.DeliveryId);
                    }

                    return TransientRequestAdmission.Joined;
                }

                PruneExecutedMarkers(now);
                if (_executedUntil.ContainsKey(request.RequestId))
                {
                    // Never run a request twice, even when its result was evicted.
                    return TransientRequestAdmission.AlreadyExecuted;
                }

                // Existing identities are classified above, including destination
                // conflicts. Never reserve or execute an unseen request addressed elsewhere.
                if (!RunnerIdMatches(request.RunnerId, runnerId))
                {
                    return TransientRequestAdmission.WrongRunner;
                }

                if (IsExpired(request, now))
                {
                    return TransientRequestAdmission.Expired;
                }

                if (_executedUntil.Count + _inFlight.Count >= MaxExecutedMarkers)
                {
                    return TransientRequestAdmission.Saturated;
                }

                _inFlight.Add(request.RequestId, new InFlightRequest(
                    fingerprint, request.ExpiresUtc.ToDateTime(), [request.DeliveryId]));
                return TransientRequestAdmission.Execute;
            }
        }

        private static ByteString Fingerprint(RelayEditorRuntimeRequestCommand request)
        {
            // Redelivery may change transport metadata, but every other command field
            // must still describe the original operation, including its deadline.
            var intent = request.Clone();
            intent.DeliveryId = string.Empty;
            intent.Attempt = 0;
            return ByteString.CopyFrom(SHA256.HashData(intent.ToByteArray()));
        }

        /// <summary>
        /// True when the request's deadline has passed or it has none. The control plane
        /// sets a deadline on every request and treats one without it as expired.
        /// </summary>
        public static bool IsExpired(RelayEditorRuntimeRequestCommand request, DateTime nowUtc)
            => request.ExpiresUtc is not { } expires ||
               (expires.Seconds == 0 && expires.Nanos == 0) ||
               expires.ToDateTime() <= nowUtc;

        private void RememberExecuted(string requestId, DateTime until)
        {
            if (_executedUntil.TryAdd(requestId, until))
            {
                _executedByDeadline.Enqueue(requestId, until);
            }
        }

        private void PruneExecutedMarkers(DateTime nowUtc)
        {
            while (_executedByDeadline.TryPeek(out var requestId, out var until) && until <= nowUtc)
            {
                _executedByDeadline.Dequeue();
                _executedUntil.Remove(requestId);
            }
        }

        /// <summary>
        /// Retains the result and returns one acknowledgement for every delivery
        /// that awaited it, each with its own delivery id.
        /// </summary>
        public IReadOnlyList<AcknowledgeEditorRuntimeRequestRequest> Complete(AcknowledgeEditorRuntimeRequestRequest result)
        {
            lock (_gate)
            {
                if (!_inFlight.Remove(result.RequestId, out var inFlight))
                {
                    return [];
                }

                completed.Set(result, inFlight.Fingerprint);
                RememberExecuted(result.RequestId, inFlight.DeadlineUtc);
                return inFlight.Deliveries
                    .Select(deliveryId =>
                    {
                        var acknowledgement = result.Clone();
                        acknowledgement.DeliveryId = deliveryId;
                        return acknowledgement;
                    })
                    .ToArray();
            }
        }
    }

    /// <summary>
    /// The shared result of one physical startup, acknowledged by every delivery of its start.
    /// How a startup ended. <paramref name="Sealing"/> is captured inside the startup, which a Stop
    /// or replacement waits for, so it always describes the runtime this command started.
    /// </summary>
    internal readonly record struct StartupOutcome(bool Ready, string Message, SessionSealingStatus? Sealing = null);

    internal sealed class TrackedCommandWork(CancellationToken shutdown)
    {
        private readonly OwnedCancellation _cancellation = new(shutdown);

        public Task? Task { get; set; }

        /// <summary>The startup's result, set only for startup work so a repeated start can join it.</summary>
        public Task<StartupOutcome>? Outcome { get; set; }
        public CancellationToken Token => _cancellation.Token;
        public required string SessionId { get; init; }
        public RuntimeIdentity RuntimeIdentity { get; init; }

        /// <summary>The sealing its start asked for; only a repeated start asking for the same joins it.</summary>
        public SessionSealing? Sealing { get; init; }

        public Task CancelAsync() => _cancellation.RequestAsync();
        public Task CompleteAsync() => _cancellation.CompleteAsync();
    }

    /// <summary>
    /// The physical operations and state one runtime command handler needs. The
    /// command stream binds them to the live services; tests bind fakes.
    /// </summary>
    internal sealed class RuntimeCommandDependencies
    {
        public required string RunnerId { get; init; }
        public required string InstanceId { get; init; }
        public required IRunnerSessionSink Sink { get; init; }
        public required Func<Func<Task>, Task> StartAcknowledgement { get; init; }
        public required RunnerCommandWorkState WorkState { get; init; }
        public required LogicalSessionState LogicalSessionState { get; init; }
        public required RuntimeIdentityRegistry RuntimeIdentities { get; init; }
        public required TransientCancellationRegistry TransientCancellation { get; init; }
        public required Func<string> GetActiveRuntimeSessionId { get; init; }
        public required Func<StartRuntimeCommand, CancellationToken, Task> StartRuntimeAndWaitForReady { get; init; }
        public required Func<string, Func<string, CancellationToken, Task>?, CancellationToken, Task> StopRuntime { get; init; }
        public required Func<string, RuntimeIdentity, Func<string, CancellationToken, Task>?, CancellationToken, Task<bool>> StopRuntimeIfIdentityMatches { get; init; }
        public required Func<string, RuntimeIdentity?> GetTrackedRuntime { get; init; }
        /// <summary>The token of the running process of exactly this runtime, if one runs.</summary>
        public required Func<string, RuntimeIdentity, Guid?> GetTrackedProcess { get; init; }
        public required Func<string, Task> StopSessionRelay { get; init; }
        public required Func<string, Task> StopArtifactUploads { get; init; }
        public required Func<string, Task> StopWorkspaceMirrors { get; init; }
        public required Action<string?> CancelSessionDownloads { get; init; }
        public required Func<string?> GetDownloadActiveSessionId { get; init; }
        public required Action<string?> SetDownloadActiveSession { get; init; }
        public required Func<Action, CancellationToken, Task> ActivateSession { get; init; }
        public required CancellationToken Shutdown { get; init; }
        /// <summary>What a started session's runtime got, for its start acknowledgement.</summary>
        public Func<string, SessionSealingStatus?>? GetSessionSealing { get; init; }
        /// <summary>Whether this runner seals a start that does not ask for it (required mode).</summary>
        public Func<StartRuntimeCommand, bool>? SealsStart { get; init; }
        /// <summary>Takes the artifacts the session lost when its runtimes' data was removed, for its stop acknowledgement.</summary>
        public Func<string, UnsavedUploads>? TakeUnsavedArtifacts { get; init; }
        /// <summary>Drops the session's durable sealed-lifecycle notes once its Stop is acknowledged with the count.</summary>
        public Action<string>? ForgetSealedLifecycle { get; init; }
    }

    internal sealed class RunnerCommandWorkState(CancellationToken shutdown)
    {
        private readonly object _gate = new();

        /// <summary>
        /// Orders start admission against Stop's fence: a start is classified and its startup
        /// published, or a Stop compared and its cutoff recorded, as one step.
        /// </summary>
        public object AdmissionGate { get; } = new();

        // The Stops in progress, by session, each the join of every Stop of the session still
        // running. Read and written only under AdmissionGate.
        private readonly Dictionary<string, Task> _stopsInProgress = new(StringComparer.Ordinal);

        /// <summary>
        /// Registers a Stop of the session as in progress; a newer start of the session waits for
        /// it before it touches the runtime. A second Stop chains onto the first. The entry stays
        /// until every Stop it joins has ended, whichever ends first. Caller holds
        /// <see cref="AdmissionGate"/>.
        /// </summary>
        public void BeginStop(StopInProgress stop)
        {
            var joined = _stopsInProgress.TryGetValue(stop.SessionId, out var earlier)
                ? Task.WhenAll(earlier, stop.Done.Task)
                : stop.Done.Task;
            stop.Joined = joined;
            _stopsInProgress[stop.SessionId] = joined;
            _ = joined.ContinueWith(
                _ =>
                {
                    lock (AdmissionGate)
                    {
                        if (_stopsInProgress.TryGetValue(stop.SessionId, out var current) && ReferenceEquals(current, joined))
                        {
                            _stopsInProgress.Remove(stop.SessionId);
                        }
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default);
        }

        /// <summary>The Stops of the session still running, if any. Caller holds <see cref="AdmissionGate"/>.</summary>
        public Task? StopsInProgress(string sessionId)
            => _stopsInProgress.TryGetValue(sessionId.Trim(), out var stops) ? stops : null;

        /// <summary>
        /// Ends a Stop's effect. A start waiting for it goes on once every Stop of the session it
        /// waits for has ended. Idempotent.
        /// </summary>
        public void EndStop(StopInProgress stop) => stop.Done.TrySetResult();
        public PromptCommandWorkSet Prompts { get; } = new(shutdown);
        // Retain successful Stops for the process lifetime, including reconnects.
        // Evicting a nonce would allow a delayed retry to stop a replacement runtime.
        public ConcurrentDictionary<Guid, StopCommandWork> Stops { get; } = new();
        // The sealing each logical session was started with, by the runtime that started it; a
        // relaunch of the session keeps it and may not weaken it.
        public ConcurrentDictionary<string, RetainedSealing> SessionSealing { get; } = new(StringComparer.Ordinal);
        private TrackedCommandWork? _activeStartup;
        // Outcomes are kept per runtime, so a delayed outcome of an older startup cannot
        // replace a newer runtime's. Only the newest few runtimes matter to the heartbeat.
        private const int RetainedStartupOutcomes = 8;
        private readonly List<RecordedStartupOutcome> _startupOutcomes = [];
        // Advances whenever a startup is published, cleared or records its outcome, so a
        // reader can tell its snapshot was overtaken by a startup that began meanwhile.
        private long _startupVersion;

        /// <summary>How the last owned startup of this exact runtime ended, if one did.</summary>
        public RecordedStartupOutcome? StartupOutcome(string sessionId, RuntimeIdentity runtime)
        {
            lock (_gate) return FindOutcome(sessionId, runtime.Normalized());
        }

        /// <summary>The unfinished startup, the runtime's recorded outcome and their version, read together.</summary>
        public (TrackedCommandWork? Startup, RecordedStartupOutcome? Outcome, long Version) ReadStartupState(
            string sessionId, RuntimeIdentity? runtime)
        {
            lock (_gate)
            {
                var startup = _activeStartup?.Task is { IsCompleted: false } ? _activeStartup : null;
                var outcome = runtime is { } current ? FindOutcome(sessionId, current.Normalized()) : null;
                return (startup, outcome, _startupVersion);
            }
        }

        public long StartupVersion
        {
            get { lock (_gate) return _startupVersion; }
        }

        private RecordedStartupOutcome? FindOutcome(string sessionId, RuntimeIdentity normalized)
            => _startupOutcomes.LastOrDefault(recorded =>
                string.Equals(recorded.SessionId, sessionId, StringComparison.Ordinal) && recorded.Runtime == normalized);

        /// <summary>
        /// Records how an owned startup of this runtime ended. <paramref name="trackedProcess"/>
        /// names a runtime's running process, if any.
        /// </summary>
        public void RecordStartupOutcome(string sessionId, RuntimeIdentity runtime, StartupOutcome outcome,
            Func<string, RuntimeIdentity, Guid?> trackedProcess, SessionSealing? requested = null)
        {
            var normalized = runtime.Normalized();
            lock (_gate)
            {
                _startupVersion++;
                var process = trackedProcess(sessionId, normalized);
                var index = _startupOutcomes.FindIndex(recorded =>
                    string.Equals(recorded.SessionId, sessionId, StringComparison.Ordinal) && recorded.Runtime == normalized);
                if (index >= 0)
                {
                    // A failed command for a process that started ready and still runs (a
                    // refusal of other sealing, say) left it as it was. Once that process is
                    // gone the failure is the runtime's own and replaces READY.
                    var existing = _startupOutcomes[index];
                    if (existing.Outcome.Ready && !outcome.Ready && process is not null && existing.Process == process) return;
                    _startupOutcomes.RemoveAt(index);
                }
                _startupOutcomes.Add(new RecordedStartupOutcome(sessionId, normalized, outcome, process, requested?.Clone()));
                if (_startupOutcomes.Count <= RetainedStartupOutcomes) return;
                // Never evict the outcome of the process still running: the heartbeat
                // describes it. A runner runs one process, so another entry is found.
                var evict = _startupOutcomes.FindIndex(recorded =>
                    recorded.Process is null || trackedProcess(recorded.SessionId, recorded.Runtime) != recorded.Process);
                _startupOutcomes.RemoveAt(evict >= 0 ? evict : 0);
            }
        }

        public TrackedCommandWork? GetActiveStartup()
        {
            lock (_gate)
            {
                return _activeStartup?.Task is { IsCompleted: false } ? _activeStartup : null;
            }
        }

        /// <summary>
        /// Publishes the new startup and returns the unfinished one it supersedes,
        /// which the caller must cancel and await before touching the runtime.
        /// </summary>
        public TrackedCommandWork? ReplaceActiveStartup(TrackedCommandWork work)
        {
            lock (_gate)
            {
                var previous = _activeStartup?.Task is { IsCompleted: false } ? _activeStartup : null;
                _activeStartup = work;
                _startupVersion++;
                return previous;
            }
        }

        public void ClearActiveStartup(TrackedCommandWork work)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeStartup, work))
                {
                    _activeStartup = null;
                    _startupVersion++;
                }
            }
        }
    }

    internal sealed class StopCommandWork(AcknowledgeSessionStopRequest acknowledgement)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public AcknowledgeSessionStopRequest Acknowledgement { get; } = acknowledgement;
        public bool Completed { get; set; }
        /// <summary>Whether this Stop took the session's count of lost artifacts.</summary>
        public bool CountTaken { get; set; }

        private int _notesClaimed;

        /// <summary>True for the first caller only: a Stop drops the session's notes at most once.</summary>
        public bool TryClaimForgetNotes() => Interlocked.Exchange(ref _notesClaimed, 1) == 0;
        public Task? AcknowledgementTask { get; set; }

        public bool Matches(AcknowledgeSessionStopRequest delivery)
        {
            // The dictionary key already compares parsed command IDs. Preserve
            // the original spelling in every replay of the acknowledgement.
            delivery = delivery.Clone();
            delivery.CommandId = Acknowledgement.CommandId;
            // Filled in from the completed stop, never by a delivery.
            delivery.UnsavedArtifactCount = Acknowledgement.UnsavedArtifactCount;
            delivery.UnsavedArtifactsUnknown = Acknowledgement.UnsavedArtifactsUnknown;
            delivery.CleanupDisposition = Acknowledgement.CleanupDisposition;
            return Acknowledgement.Equals(delivery);
        }
    }

    /// <summary>How a delivered start relates to the work this runner already has.</summary>
    internal enum StartAdmission
    {
        /// <summary>Older work: at or below a Stop cutoff or the eviction floor, or older than the newest start adopted.</summary>
        Stale,

        /// <summary>The runtime of the newest start adopted: join its startup or replay its acknowledgement.</summary>
        Same,

        /// <summary>Newer than every start adopted: it replaces whatever runs.</summary>
        Newer,
    }

    internal sealed class LogicalSessionState(string initialSessionId, int maxStopCutoffs = 16_384)
    {
        private sealed record State(string SessionId, RuntimeIdentity? RuntimeIdentity);

        private State _state = new(initialSessionId?.Trim() ?? string.Empty, null);

        public string GetActiveSessionId() => Volatile.Read(ref _state).SessionId;

        public bool TryGetRuntime(string? sessionId, out RuntimeIdentity runtimeIdentity)
        {
            var current = Volatile.Read(ref _state);
            if (current.RuntimeIdentity is { } identity &&
                string.Equals(current.SessionId, sessionId?.Trim() ?? string.Empty, StringComparison.Ordinal))
            {
                runtimeIdentity = identity;
                return true;
            }

            runtimeIdentity = default;
            return false;
        }

        /// <summary>
        /// Swaps the session's runtime identity from exactly <paramref name="expected"/>
        /// to <paramref name="replacement"/>; a different identity is left untouched.
        /// </summary>
        public bool ReplaceRuntimeIfMatches(string? sessionId, RuntimeIdentity expected, RuntimeIdentity replacement)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (!string.Equals(current.SessionId, normalizedSessionId, StringComparison.Ordinal) ||
                    current.RuntimeIdentity is not { } currentRuntime || !currentRuntime.SameRuntime(expected))
                {
                    return false;
                }

                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, new State(normalizedSessionId, replacement), current),
                    current))
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Classifies a delivered start against the Stop cutoffs and the newest start adopted,
        /// and adopts it as the newest when it is newer. Older work is refused; the runtime of
        /// the newest start is the same work again; anything else must be newer. Orders come from
        /// one sequence for every session, so this also orders starts of different sessions.
        /// </summary>
        public StartAdmission AdmitStart(string? sessionId, RuntimeIdentity candidate)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            if (normalizedSessionId.Length == 0 || !candidate.IsValid)
            {
                return StartAdmission.Stale;
            }

            lock (_fenceGate)
            {
                if (IsFencedByStop(normalizedSessionId, candidate.RuntimeOrder))
                {
                    return StartAdmission.Stale;
                }

                if (_newestStart is { } newest)
                {
                    if (newest.Runtime.SameRuntime(candidate))
                    {
                        // A runtime id names one start of one session at one order.
                        return string.Equals(newest.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                               newest.Runtime.RuntimeOrder == candidate.RuntimeOrder
                            ? StartAdmission.Same
                            : StartAdmission.Stale;
                    }

                    if (candidate.RuntimeOrder <= newest.Runtime.RuntimeOrder)
                    {
                        return StartAdmission.Stale;
                    }
                }

                _newestStart = (normalizedSessionId, candidate);
                return StartAdmission.Newer;
            }
        }

        /// <summary>
        /// Makes <paramref name="candidate"/> the session's runtime, replacing another session's.
        /// Refused when a newer start was adopted or a Stop fenced it since it was admitted.
        /// </summary>
        public bool TryAdoptRuntime(string? sessionId, RuntimeIdentity candidate)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            candidate = candidate with { RuntimeId = candidate.RuntimeId?.Trim() ?? string.Empty };
            if (normalizedSessionId.Length == 0 || !candidate.IsValid)
            {
                return false;
            }

            lock (_fenceGate)
            {
                if (IsFencedByStop(normalizedSessionId, candidate.RuntimeOrder) ||
                    _newestStart is not { } newest || !newest.Runtime.SameRuntime(candidate))
                {
                    return false;
                }

                Volatile.Write(ref _state, new State(normalizedSessionId, candidate));
                RecordAdopted(normalizedSessionId, candidate.RuntimeOrder);
                return true;
            }
        }

        public void ClearIfMatches(string? sessionId)
        {
            var expected = sessionId?.Trim() ?? string.Empty;
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (!string.Equals(current.SessionId, expected, StringComparison.Ordinal))
                {
                    return;
                }

                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, new State(string.Empty, null), current),
                    current))
                {
                    return;
                }
            }
        }

        public void ClearIfMatches(string? sessionId, RuntimeIdentity expectedRuntime)
        {
            var expectedSessionId = sessionId?.Trim() ?? string.Empty;
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (!string.Equals(current.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                    current.RuntimeIdentity is not { } currentRuntime || !currentRuntime.SameRuntime(expectedRuntime))
                {
                    return;
                }

                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, new State(string.Empty, null), current),
                    current))
                {
                    return;
                }
            }
        }

        /// <summary>
        /// Records a Stop's cutoff, so a delayed or redelivered start of the session at or below
        /// it cannot bring the session back. The Stop may arrive before the session's start.
        /// </summary>
        public void RecordStopCutoff(string? sessionId, ulong upToRuntimeOrder)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            if (normalizedSessionId.Length == 0)
            {
                return;
            }

            lock (_fenceGate)
            {
                if (_stopCutoffs.TryGetValue(normalizedSessionId, out var known))
                {
                    _stopCutoffs[normalizedSessionId] = Math.Max(known, upToRuntimeOrder);
                    return;
                }

                _stopCutoffs[normalizedSessionId] = upToRuntimeOrder;
                _stopOrder.Enqueue(normalizedSessionId);
                while (_stopOrder.Count > Math.Max(1, maxStopCutoffs))
                {
                    // An evicted session's old start must still be refused, so the floor rises
                    // to its cutoff. Orders are shared by every session: the floor only refuses
                    // starts minted before a Stop that many Stops ago.
                    if (_stopCutoffs.Remove(_stopOrder.Dequeue(), out var evicted))
                    {
                        _stopFloor = Math.Max(_stopFloor, evicted);
                    }
                }
            }
        }

        /// <summary>
        /// Clears the session after its Stop, unless it holds a runtime newer than the Stop's
        /// cutoff: a start adopted while the Stop ran is not the Stop's to clear.
        /// </summary>
        public void ClearAfterStop(string? sessionId, ulong upToRuntimeOrder)
        {
            var expected = sessionId?.Trim() ?? string.Empty;
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (!string.Equals(current.SessionId, expected, StringComparison.Ordinal) ||
                    current.RuntimeIdentity is { } runtime && runtime.RuntimeOrder > upToRuntimeOrder)
                {
                    return;
                }

                if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _state, new State(string.Empty, null), current),
                    current))
                {
                    return;
                }
            }
        }

        // Stop cutoffs by session, in bounded memory. Evicting one raises the floor to its
        // cutoff, so nothing it refused becomes acceptable.
        private readonly object _fenceGate = new();
        private readonly Dictionary<string, ulong> _stopCutoffs = new(StringComparer.Ordinal);
        private readonly Queue<string> _stopOrder = new();
        private ulong _stopFloor;

        // The highest order each session adopted here, in the same bound. Evicting one raises a
        // floor to its order, so an evicted session still counts as having adopted that much.
        private readonly Dictionary<string, ulong> _highestAdopted = new(StringComparer.Ordinal);
        private readonly Queue<string> _adoptedOrder = new();
        private ulong _adoptedFloor;

        /// <summary>
        /// True when the session adopted a runtime above <paramref name="upToRuntimeOrder"/> here,
        /// even one that has since exited or been pre-empted; for an evicted session, when that
        /// is possible. A Stop with this cutoff then does not cover all of the session's runtimes.
        /// </summary>
        public bool AdoptedAbove(string? sessionId, ulong upToRuntimeOrder)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            lock (_fenceGate)
            {
                return _highestAdopted.TryGetValue(normalizedSessionId, out var highest)
                    ? highest > upToRuntimeOrder
                    : _adoptedFloor > upToRuntimeOrder;
            }
        }

        // Caller holds _fenceGate.
        private void RecordAdopted(string sessionId, ulong runtimeOrder)
        {
            if (_highestAdopted.TryGetValue(sessionId, out var known))
            {
                _highestAdopted[sessionId] = Math.Max(known, runtimeOrder);
                return;
            }

            _highestAdopted[sessionId] = runtimeOrder;
            _adoptedOrder.Enqueue(sessionId);
            while (_adoptedOrder.Count > Math.Max(1, maxStopCutoffs))
            {
                if (_highestAdopted.Remove(_adoptedOrder.Dequeue(), out var evicted))
                {
                    _adoptedFloor = Math.Max(_adoptedFloor, evicted);
                }
            }
        }

        // The newest start adopted on this runner, of any session.
        private (string SessionId, RuntimeIdentity Runtime)? _newestStart;

        private bool IsFencedByStop(string sessionId, ulong runtimeOrder)
            => runtimeOrder <= _stopFloor ||
               (_stopCutoffs.TryGetValue(sessionId, out var cutoff) && runtimeOrder <= cutoff);
    }

    internal static TimeSpan CommandStreamKeepAliveInterval { get; set; } = TimeSpan.FromSeconds(20);
    internal static TimeSpan LocalModelKeepAliveInterval { get; set; } = TimeSpan.FromMinutes(1);
    internal static IReadOnlyList<TimeSpan> PromptArtifactRefreshDelays { get; set; } =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5)
    ];

    /// <summary>
    /// Long-lived Offloadr API supervisor that owns explicit status, commands, session-event, and ops lanes.
    /// </summary>
    public static async Task CommandListenerLoopAsync(
        RunnerControlService.RunnerControlServiceClient commandClient,
        IRunnerSessionSink sessionSink,
        string runnerId,
        string instanceId,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        string version,
        SessionProcessManager sessionManager,
        ModelDownloadService downloadService,
        ComfySessionEventRelay sessionEventRelay,
        SessionProcessLogRelay sessionProcessLogRelay,
        RunnerLogRelay runnerLogRelay,
        SessionRuntimeTelemetryRelay sessionRuntimeTelemetryRelay,
        GpuPowerCommandExecutor gpuPowerLimitService,
        ArtifactUploadService artifactUploadService,
        WorkspaceMirrorService workspaceMirrorService,
        LogicalSessionState logicalSessionState,
        RunnerResources resources,
        IReadOnlyList<string> supportedEditorTemplates,
        Func<LocalModelSnapshot>? localModelSnapshotProvider,
        Action<LocalModelSnapshot>? localModelSnapshotObserver,
        Func<SessionProcessManager.SessionPaths?, IEnumerable<ModelDownloadRequest>, bool, CancellationToken, Task>? localModelProjectionReconciler,
        TimeSpan localModelsScanInterval,
        RuntimeIdentityRegistry runtimeIdentities,
        CancellationToken shutdown,
        Action<RegisterRunnerResponse>? onProtocolRejected = null,
        string launchId = "")
    {
        ArgumentNullException.ThrowIfNull(commandClient);
        ArgumentNullException.ThrowIfNull(sessionSink);
        await using var lifetime = new RunnerControlLifetime(shutdown);
        shutdown = lifetime.Token;
        var initialStatusApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandWorkState = new RunnerCommandWorkState(shutdown);
        // ComfyUI's terminal event (success, error or interrupted) ends an
        // accepted prompt's model demand; a global interrupt alone cannot tell
        // which accepted prompt it stopped.
        void WithdrawFinishedPromptDemand(ComfySessionEventRelay.PromptTerminalEvent finished)
            => commandWorkState.Prompts.WithdrawDemand(prompt => IsFinishedPrompt(prompt, finished));
        LocalModelSnapshot? initialLocalModelSnapshot = null;
        await lifetime.RunAsync(
            attach: () =>
            {
                sessionEventRelay.AttachSink(sessionSink);
                sessionEventRelay.PromptFinished += WithdrawFinishedPromptDemand;
                sessionProcessLogRelay.AttachSink(sessionSink);
                // A first registration creates the runner, so lines before it would be refused.
                runnerLogRelay.AttachSink(sessionSink, initialStatusApplied.Task);
                sessionRuntimeTelemetryRelay.AttachSink(sessionSink);

                initialLocalModelSnapshot = PrepareInitialLocalModelSnapshot(localModelSnapshotProvider, localModelSnapshotObserver);

                downloadService.RuntimeResolver = sessionId => runtimeIdentities.TryGet(sessionId, out var identity) ? identity : null;
                downloadService.ProgressReporter = progress => lifetime.ReportProgressAsync(async progressCancellation =>
                {
                    RuntimeIdentity? reportedRuntime = null;
                    try
                    {
                        var request = new ReportModelDownloadRequest
                        {
                            RunnerId = runnerId,
                            SessionId = progress.SessionId,
                            ModelId = progress.ModelId,
                            Filename = progress.Filename,
                            DestinationPath = progress.DestinationPath,
                            BytesDownloaded = progress.BytesDownloaded,
                            TotalBytes = progress.TotalBytes,
                            Percent = double.IsFinite(progress.Percent) ? Math.Clamp(progress.Percent, 0d, 100d) : 0d,
                            BytesPerSecond = progress.BytesPerSecond,
                            AverageBytesPerSecond = progress.AverageBytesPerSecond,
                            State = progress.State,
                            Message = progress.Message ?? string.Empty
                        };
                        // Report under the runtime the download was registered for. Only that identity
                        // may be treated as revoked: the session may since have a replacement runtime.
                        if (progress.Runtime is { IsValid: true } registeredRuntime)
                        {
                            request.Runtime = registeredRuntime.ToRef();
                            reportedRuntime = registeredRuntime;
                        }
                        else if (runtimeIdentities.TryGet(progress.SessionId, out var currentRuntime))
                        {
                            // Registered before the session had a runtime: label it with the current
                            // one so it is accepted, but never revoke a runtime on its rejection.
                            request.Runtime = currentRuntime.ToRef();
                        }
                        await sessionSink.ReportModelDownloadAsync(request, progressCancellation).ConfigureAwait(false);
                    }
                    catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
                    {
                        RunnerLog.Error($"Model download progress failed; errorClass={ex.GetType().Name}.");
                        await HandleRevokedRuntimeReportAsync(
                            progress.SessionId,
                            reportedRuntime,
                            logicalSessionState,
                            runtimeIdentities,
                            sessionManager.GetActiveSessionId,
                            sessionManager.StopSessionAsync,
                            sessionManager.StopSessionIfRuntimeMatchesAsync,
                            sessionEventRelay.StopSessionAsync,
                            artifactUploadService.StopSessionAsync,
                            workspaceMirrorService.StopSessionAsync,
                            downloadService.CancelSession,
                            downloadService.GetActiveSessionId,
                            downloadService.SetActiveSession).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (progressCancellation.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        RunnerLog.Error($"Model download progress failed; errorClass={ex.GetType().Name}.");
                    }
                });
            },
            status: token => RunnerRegistrationLoop.RunAsync(
                sessionSink.RegisterRunnerAsync,
                () => BuildStatusUpdateRequest(
                    runnerId,
                    instanceId,
                    version,
                    resources,
                    () => DescribeRuntimeStatus(
                        logicalSessionState,
                        commandWorkState,
                        sessionManager.IsStopping,
                        sessionManager.TrackedProcess),
                    supportedEditorTemplates,
                    sessionManager.DescribeSealedStorage,
                    launchId),
                minBackoff,
                maxBackoff,
                initialStatusApplied,
                RunnerLog.CreateLogger(nameof(ServiceClientManager)),
                token,
                response =>
                {
                    // Cancel this lifetime before the required status lane returns;
                    // the host token's callback would only cancel it later.
                    _ = lifetime.RequestShutdownAsync();
                    onProtocolRejected?.Invoke(response);
                }),
            localModels: token => localModelSnapshotProvider is null
                ? Task.CompletedTask
                : RunLocalModelSyncLoopAsync(
                    sessionSink.SyncLocalModelsAsync,
                    token,
                    runnerId,
                    localModelsScanInterval,
                    minBackoff,
                    maxBackoff,
                    localModelSnapshotProvider,
                    localModelSnapshotObserver,
                    initialStatusApplied.Task,
                    initialLocalModelSnapshot),
            commands: token => RunCommandStreamLoopAsync(
                commandClient,
                sessionSink,
                token,
                runnerId,
                instanceId,
                version,
                minBackoff,
                maxBackoff,
                initialStatusApplied.Task,
                sessionManager,
                downloadService,
                sessionEventRelay,
                artifactUploadService,
                workspaceMirrorService,
                gpuPowerLimitService,
                localModelProjectionReconciler,
                commandWorkState,
                logicalSessionState,
                runtimeIdentities,
                lifetime.RequestShutdownAsync),
            drainGpu: gpuPowerLimitService.CancelAndDrainAsync,
            drainPrompts: () => commandWorkState.Prompts.CancelAndWaitAsync(_ => true),
            drainStartup: () => CancelTrackedWorkAsync(commandWorkState.GetActiveStartup()),
            detach:
            [
                () => downloadService.ProgressReporter = null,
                () => downloadService.RuntimeResolver = null,
                () => downloadService.SetActiveSession(null),
                () => sessionEventRelay.PromptFinished -= WithdrawFinishedPromptDemand,
                () => sessionEventRelay.DetachSink(sessionSink),
                () => sessionProcessLogRelay.DetachSink(sessionSink),
                () => runnerLogRelay.DetachSink(sessionSink),
                () => sessionRuntimeTelemetryRelay.DetachSink(sessionSink)
            ]).ConfigureAwait(false);
    }

    internal static async Task RunLocalModelSyncLoopAsync(
        Func<SyncLocalModelsRequest, CancellationToken, Task<SyncLocalModelsResponse>> syncLocalModelsAsync,
        CancellationToken shutdown,
        string runnerId,
        TimeSpan scanInterval,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        Func<LocalModelSnapshot> snapshotProvider,
        Action<LocalModelSnapshot>? snapshotObserver,
        Task initialStatusApplied,
        LocalModelSnapshot? initialSnapshot = null)
    {
        ArgumentNullException.ThrowIfNull(syncLocalModelsAsync);
        if (snapshotProvider == null) throw new ArgumentNullException(nameof(snapshotProvider));

        try
        {
            await initialStatusApplied.WaitAsync(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;
        var rng = new Random();
        var keepAliveInterval = LocalModelKeepAliveInterval;
        var lastSentDigest = string.Empty;
        var lastObservedDigest = initialSnapshot?.Digest ?? string.Empty;
        var hasSnapshot = initialSnapshot.HasValue;
        LocalModelSnapshot cachedSnapshot = initialSnapshot.GetValueOrDefault();
        var nextScanUtc = hasSnapshot ? DateTime.UtcNow + scanInterval : DateTime.MinValue;
        var nextKeepAliveUtc = DateTime.MinValue;
        var lastRequestWasFullSnapshot = false;

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (!hasSnapshot || now >= nextScanUtc)
                {
                    var scannedSnapshot = snapshotProvider();
                    cachedSnapshot = scannedSnapshot;
                    hasSnapshot = true;
                    nextScanUtc = now + scanInterval;

                    if (snapshotObserver is not null)
                    {
                        try
                        {
                            snapshotObserver(scannedSnapshot);
                        }
                        catch (Exception ex)
                        {
                            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to reconcile local models into session roots: {ex.Message}");
                        }
                    }

                    if (!string.Equals(lastObservedDigest, scannedSnapshot.Digest, StringComparison.Ordinal))
                    {
                        RunnerLog.Info(nameof(ServiceClientManager), BuildLocalModelSummary(scannedSnapshot));
                        lastObservedDigest = scannedSnapshot.Digest;
                    }
                }

                if (!hasSnapshot)
                {
                    throw new InvalidOperationException("Local model snapshot cache is not initialized.");
                }

                var snapshot = cachedSnapshot;
                var inventoryChanged = !string.Equals(lastSentDigest, snapshot.SelectionDigest, StringComparison.Ordinal);
                var fullSnapshot = inventoryChanged;
                var shouldSync = fullSnapshot
                                 || now >= nextKeepAliveUtc;

                if (shouldSync)
                {
                    lastRequestWasFullSnapshot = fullSnapshot;
                    var response = await syncLocalModelsAsync(
                        BuildLocalModelsRequest(runnerId, snapshot, fullSnapshot),
                        shutdown).ConfigureAwait(false);
                    if (inventoryChanged)
                    {
                        RunnerLog.Info(
                            nameof(ServiceClientManager),
                            $"Local model sync accepted={response.AcceptedCount} digest={snapshot.SelectionDigest[..Math.Min(12, snapshot.SelectionDigest.Length)]}");
                    }

                    lastSentDigest = snapshot.SelectionDigest;
                    nextKeepAliveUtc = DateTime.UtcNow + keepAliveInterval;
                }

                attempt = 0;
                var delay = ComputeLocalModelLoopDelay(DateTime.UtcNow, nextScanUtc, nextKeepAliveUtc);
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex)
            {
                if (ex.StatusCode == StatusCode.InvalidArgument && !lastRequestWasFullSnapshot)
                {
                    RunnerLog.Warning(
                        nameof(ServiceClientManager),
                        "Digest-only local model refresh was rejected; forcing a full local model resync.");
                    lastSentDigest = string.Empty;
                    nextKeepAliveUtc = DateTime.MinValue;
                    attempt = 0;
                    continue;
                }

                attempt++;
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Local model sync RPC error attempt {attempt}: {ex.StatusCode} {ex.Message}");
                var delay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                try
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                attempt++;
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Local model sync failed: {ex.Message}");
                var delay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                try
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    internal static LocalModelSnapshot? PrepareInitialLocalModelSnapshot(
        Func<LocalModelSnapshot>? snapshotProvider,
        Action<LocalModelSnapshot>? snapshotObserver)
    {
        if (snapshotProvider is null)
        {
            return null;
        }

        try
        {
            var snapshot = snapshotProvider();
            if (snapshotObserver is not null)
            {
                snapshotObserver(snapshot);
            }

            RunnerLog.Info(nameof(ServiceClientManager), BuildLocalModelSummary(snapshot));
            return snapshot;
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to prepare initial local model snapshot: {ex.Message}");
            return null;
        }
    }

    private static async Task RunCommandStreamLoopAsync(
        RunnerControlService.RunnerControlServiceClient commandClient,
        IRunnerSessionSink sessionSink,
        CancellationToken shutdown,
        string runnerId,
        string instanceId,
        string version,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        Task initialStatusApplied,
        SessionProcessManager sessionManager,
        ModelDownloadService downloadService,
        ComfySessionEventRelay sessionEventRelay,
        ArtifactUploadService artifactUploadService,
        WorkspaceMirrorService workspaceMirrorService,
        GpuPowerCommandExecutor gpuPowerLimitService,
        Func<SessionProcessManager.SessionPaths?, IEnumerable<ModelDownloadRequest>, bool, CancellationToken, Task>? localModelProjectionReconciler,
        RunnerCommandWorkState commandWorkState,
        LogicalSessionState logicalSessionState,
        RuntimeIdentityRegistry runtimeIdentities,
        Func<Task> requestParentShutdown)
    {
        ArgumentNullException.ThrowIfNull(commandClient);
        ArgumentNullException.ThrowIfNull(sessionSink);

        try
        {
            await initialStatusApplied.WaitAsync(shutdown).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            return;
        }

        var attempt = 0;
        var rng = new Random();
        var transientDeliveries = new TransientRequestDeliveries(new TransientAcknowledgementCache());
        using var ordinaryControlGate = new SemaphoreSlim(1, 1);
        using var readGate = new SemaphoreSlim(4, 4);
        await using var commandTasks = new RunnerCommandLifetime(shutdown, requestParentShutdown);
        shutdown = commandTasks.Token;
        await using var transientCancellation = new TransientCancellationRegistry(shutdown);
        var runtimeCommands = new RuntimeCommandDependencies
        {
            RunnerId = runnerId,
            InstanceId = instanceId,
            Sink = sessionSink,
            StartAcknowledgement = commandTasks.Start,
            WorkState = commandWorkState,
            LogicalSessionState = logicalSessionState,
            RuntimeIdentities = runtimeIdentities,
            TransientCancellation = transientCancellation,
            GetActiveRuntimeSessionId = sessionManager.GetActiveSessionId,
            StartRuntimeAndWaitForReady = (start, token) => StartSessionAndWaitForReadyAsync(
                start,
                sessionManager,
                artifactUploadService,
                workspaceMirrorService,
                downloadService,
                localModelProjectionReconciler,
                token),
            StopRuntime = sessionManager.StopSessionAsync,
            StopRuntimeIfIdentityMatches = sessionManager.StopSessionIfRuntimeMatchesAsync,
            GetTrackedRuntime = sessionId => sessionManager.TryGetRuntimeIdentity(sessionId, out var tracked) ? tracked : null,
            GetTrackedProcess = sessionManager.TrackedProcess,
            StopSessionRelay = sessionEventRelay.StopSessionAsync,
            StopArtifactUploads = artifactUploadService.StopSessionAsync,
            StopWorkspaceMirrors = workspaceMirrorService.StopSessionAsync,
            CancelSessionDownloads = downloadService.CancelSession,
            GetDownloadActiveSessionId = downloadService.GetActiveSessionId,
            SetDownloadActiveSession = downloadService.SetActiveSession,
            ActivateSession = gpuPowerLimitService.ActivateSessionAsync,
            Shutdown = shutdown,
            GetSessionSealing = sessionManager.GetSessionSealingStatus,
            SealsStart = start => start.Sealing?.Required != true && sessionManager.ShouldSeal(start),
            // A Stop covers every runtime of its session up to its cutoff, and a session keeps one
            // lifecycle generation: it takes the whole session's count and notes.
            TakeUnsavedArtifacts = sessionId => artifactUploadService.TakeUnsavedUploads(sessionId),
            ForgetSealedLifecycle = sessionId => sessionManager.ForgetSealedLifecycle(sessionId, 0),
        };

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                using var call = commandClient.CommandStream(
                    new CommandStreamRequest
                    {
                        RunnerId = runnerId,
                        Version = version,
                        InstanceId = instanceId,
                        ProtocolVersion = RunnerProtocolVersion.Current,
                    },
                    cancellationToken: shutdown);
                RunnerLog.Info(nameof(ServiceClientManager), "Runner commands stream (grpc) connected.");
                attempt = 0;

                while (await call.ResponseStream.MoveNext(shutdown).ConfigureAwait(false))
                {
                    var evt = call.ResponseStream.Current;
                    if (evt.Keepalive is not null)
                    {
                        continue;
                    }

                    if (evt.StartRuntime != null && !string.IsNullOrWhiteSpace(evt.StartRuntime.SessionId))
                    {
                        // Admission runs here, before the next command is read: a repeated start
                        // joins or replays the first, and a Stop read after it sees its startup.
                        RunnerLog.Info(nameof(ServiceClientManager), $"StartRuntime event: session={evt.StartRuntime.SessionId} user={evt.StartRuntime.User}");
                        var start = evt.StartRuntime;
                        _ = commandTasks.Start(() => HandleStartRuntimeCommand(start, runtimeCommands));
                        continue;
                    }

                    if (evt.StopSession != null && !string.IsNullOrWhiteSpace(evt.StopSession.SessionId))
                    {
                        RunnerLog.Info(nameof(ServiceClientManager), $"StopSession event: session={evt.StopSession.SessionId} user={evt.StopSession.User}");
                        await HandleStopSessionCommandAsync(evt.StopSession, runtimeCommands).ConfigureAwait(false);
                        continue;
                    }

                    if (evt.SetGpuPowerLimit != null && !string.IsNullOrWhiteSpace(evt.SetGpuPowerLimit.CommandId))
                    {
                        // Physical work and acknowledgement retry must not block Stop delivery.
                        _ = commandTasks.Start(() => gpuPowerLimitService.HandleAsync(
                            evt.SetGpuPowerLimit, runnerId, logicalSessionState.GetActiveSessionId,
                            () => runtimeIdentities.LifecycleGeneration(evt.SetGpuPowerLimit.SessionId),
                            sessionSink.GrantGpuPowerExecutionAsync, sessionSink.AckGpuPowerLimitAsync, shutdown));
                        continue;
                    }

                    if (evt.RelayEditorRuntimeRequest != null && !string.IsNullOrWhiteSpace(evt.RelayEditorRuntimeRequest.RequestId))
                    {
                        var runtimeRequest = evt.RelayEditorRuntimeRequest;
                        _ = commandTasks.Start(() => DispatchTransientEditorRuntimeRequest(
                            runtimeRequest,
                            runnerId,
                            transientDeliveries,
                            transientCancellation,
                            (request, token) => ExecuteTransientEditorRuntimeRequestAsync(
                                request,
                                runnerId,
                                sessionManager,
                                sessionEventRelay,
                                ordinaryControlGate,
                                readGate,
                                () =>
                                {
                                    if (request.Kind == EditorRuntimeRequestKind.Control &&
                                        ComfyRuntimeRoutes.IsGlobalInterrupt(request.Path, request.Body.Span))
                                    {
                                        commandWorkState.Prompts.Cancel(prompt =>
                                            TargetsPromptRuntime(request, prompt) && !prompt.StreamsHttpResponse);
                                    }
                                },
                                token,
                                (response, sentToken) => InterruptBlockedReadsForCancelAsync(
                                    request,
                                    response,
                                    sessionManager.ReadComfyPromptQueueAsync,
                                    downloadService.InterruptSessionReads,
                                    sentToken)),
                            sessionSink.AckEditorRuntimeRequestAsync,
                            shutdown));
                        continue;
                    }

                    if (evt.SubmitPrompt != null && !string.IsNullOrWhiteSpace(evt.SubmitPrompt.HandoffId))
                    {
                        var prompt = evt.SubmitPrompt;
                        // A redelivery of a handoff this process is running, or already ran, starts nothing.
                        commandWorkState.Prompts.Start(prompt, ShouldStreamHttpResponse(prompt), (captured, physicalCancellation, demandCancellation) =>
                        {
                            var handoffId = captured.HandoffId;
                            var runtime = PromptHandoff.Runtime(captured);
                            return HandleSubmitEditorActionAsync(
                            (ack, _) => QueuePromptAcknowledgementAsync(commandTasks, sessionSink, ack, shutdown),
                            (events, token) => sessionSink.ReportSessionEventsAsync(events, token),
                            captured,
                            runnerId,
                            instanceId,
                            sessionManager.IsPromptRuntimeCurrent,
                            sessionSink.GrantPromptExecutionAsync,
                            (sessionId, editorSid, clientId, token) => sessionEventRelay.EnsureBridgeAsync(
                                sessionId,
                                editorSid,
                                clientId,
                                runtime,
                                captured.WebsocketClientMessage.ToByteArray(),
                                token),
                            downloadService.SetActiveSession,
                            (sessionId, editorSid, owner, token) => artifactUploadService.ActivateSessionAsync(sessionId, editorSid, owner, token),
                            (sessionId, editorSid, owner, artifacts, token) => artifactUploadService.SeedSessionAsync(sessionId, editorSid, owner, artifacts, token),
                            artifactUploadService.CaptureSessionRefresh,
                            downloadService.RegisterDownloads,
                            // As the prompt's own session: another may be active by the time its downloads are prepared.
                            (downloads, token, highPriority) => downloadService.PreparePromptDownloadsAsync(downloads, token, highPriority, captured.SessionId),
                            (submitPromptCommand, responseChunkSink, onNativeRequestAttempt, token) => sessionManager.SubmitEditorActionAsync(submitPromptCommand, responseChunkSink, token, onNativeRequestAttempt),
                            (sessionId, clientId, promptId, _) => sessionEventRelay.MapPrompt(sessionId, clientId, captured, promptId),
                            physicalCancellation,
                            localModelProjectionReconciler,
                            sessionId => sessionManager.TryGetSessionPaths(sessionId, out var paths) ? paths : null,
                            demandCancellation,
                            CreatePromptQueueProbe(sessionManager.ReadComfyPromptQueueAsync, captured.SessionId, runtime),
                            settling => commandWorkState.Prompts.HoldDemand(handoffId, settling),
                            () => commandWorkState.Prompts.MarkGranted(handoffId),
                            hostShutdown: shutdown,
                            // Off the prompt's own work, so Stop never waits for a grant.
                            settleInBackground: settle => _ = commandTasks.Start(settle));
                        });
                    }
                }

                attempt++;
                var delay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                RunnerLog.Warning(nameof(ServiceClientManager), $"Runner commands stream closed. Reconnecting in {delay.TotalSeconds:F1}s");
                await Task.Delay(delay, shutdown).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && shutdown.IsCancellationRequested)
            {
                // Host shutdown cancels the call; that is not a stream failure.
                break;
            }
            catch (RpcException ex)
            {
                attempt++;
                var delay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Runner commands stream RPC error attempt {attempt}: {ex.StatusCode} {ex.Message}");
                RunnerLog.Warning(nameof(ServiceClientManager), $"Retrying runner commands stream in {delay.TotalSeconds:F1}s");
                try
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                attempt++;
                var delay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Unexpected error in runner commands stream attempt {attempt}: {ex.Message}");
                RunnerLog.Warning(nameof(ServiceClientManager), $"Retrying runner commands stream in {delay.TotalSeconds:F1}s");
                try
                {
                    await Task.Delay(delay, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    break;
                }
            }
        }

    }

    internal static Task QueuePromptAcknowledgementAsync(
        RunnerCommandLifetime commandTasks,
        IRunnerSessionSink sessionSink,
        AcknowledgePromptResultRequest acknowledgement,
        CancellationToken shutdown)
    {
        // Construct the retry factory outside the physical prompt callback's
        // closure, so it does not retain that callback's complete command body.
        var captured = acknowledgement.Clone();
        _ = commandTasks.Start(() => AcknowledgeRuntimeCommandWithRetryAsync(
            sessionSink.AckSubmitEditorActionAsync, captured, captured.HandoffId, shutdown));
        return Task.CompletedTask;
    }

    internal static TimeSpan ComputeLocalModelLoopDelay(DateTime nowUtc, DateTime nextScanUtc, DateTime nextKeepAliveUtc)
    {
        var nextWakeUtc = nextScanUtc <= nextKeepAliveUtc ? nextScanUtc : nextKeepAliveUtc;
        var delay = nextWakeUtc - nowUtc;
        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
    }

    internal static async Task<string?> PreemptActiveSessionIfNeededAsync(
        string incomingSessionId,
        Func<string> getActiveSessionId,
        Func<string, Func<string, CancellationToken, Task>?, CancellationToken, Task> stopSession,
        Func<string, Task> stopSessionRelay,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        Action<string?> cancelSessionDownloads,
        Func<string?> getDownloadActiveSessionId,
        Action<string?> setDownloadActiveSessionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(incomingSessionId))
        {
            return null;
        }

        var incomingId = incomingSessionId.Trim();
        var activeSessionId = getActiveSessionId()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(activeSessionId) || string.Equals(activeSessionId, incomingId, StringComparison.Ordinal))
        {
            return null;
        }

        RunnerLog.Warning(
            nameof(ServiceClientManager),
            $"Preempting stale active session '{activeSessionId}' before starting '{incomingId}'.");

        var activeDownloadSessionId = getDownloadActiveSessionId();
        if (string.Equals(activeDownloadSessionId, activeSessionId, StringComparison.Ordinal))
        {
            cancelSessionDownloads(activeSessionId);
            setDownloadActiveSessionId(null);
        }

        Task? sidecars = null;
        Task StopSidecarsAsync(string sid, CancellationToken token)
            => sidecars ??= StopArtifactAndWorkspaceMirrorsAsync(sid, stopArtifactUploads, stopWorkspaceMirrors, token);
        ExceptionDispatchInfo? failure = null;
        try { await stopSession(activeSessionId, StopSidecarsAsync, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        try { await StopSidecarsAsync(activeSessionId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
        try { await stopSessionRelay(activeSessionId).ConfigureAwait(false); }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Preempted relay cleanup ended with {error.GetType().Name}.");
            failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("Session relay cleanup failed."));
        }
        failure?.Throw();

        return activeSessionId;
    }

    /// <summary>
    /// Handles the control plane revoking a runtime that made a report. The
    /// revocation applies only to the identity sent with the report: when the
    /// session has since moved to another runtime, nothing is cleared or stopped.
    /// </summary>
    internal static async Task HandleRevokedRuntimeReportAsync(
        string? sessionId,
        RuntimeIdentity? reportedRuntime,
        LogicalSessionState logicalSessionState,
        RuntimeIdentityRegistry runtimeIdentities,
        Func<string> getActiveSessionId,
        Func<string, Func<string, CancellationToken, Task>?, CancellationToken, Task> stopSession,
        Func<string, RuntimeIdentity, Func<string, CancellationToken, Task>?, CancellationToken, Task<bool>> stopSessionIfRuntimeMatches,
        Func<string, Task> stopSessionRelay,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        Action<string?> cancelSessionDownloads,
        Func<string?> getDownloadActiveSessionId,
        Action<string?> setDownloadActiveSessionId)
    {
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (normalizedSessionId.Length == 0)
        {
            return;
        }

        var hasCurrent = runtimeIdentities.TryGet(normalizedSessionId, out var currentRuntime);
        if (reportedRuntime is { } reported ? hasCurrent && !currentRuntime.SameRuntime(reported) : hasCurrent)
        {
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Ignoring revocation of a superseded runtime of session {normalizedSessionId}.");
            return;
        }

        try
        {
            if (reportedRuntime is { } revoked)
            {
                await AbortRevokedSessionAsync(
                    normalizedSessionId,
                    getActiveSessionId,
                    async (sid, beforeCleanup, token) =>
                        await stopSessionIfRuntimeMatches(sid, revoked, beforeCleanup, token).ConfigureAwait(false),
                    stopSessionRelay,
                    stopArtifactUploads,
                    stopWorkspaceMirrors,
                    cancelSessionDownloads,
                    getDownloadActiveSessionId,
                    setDownloadActiveSessionId).ConfigureAwait(false);
                ClearRuntimeSessionStateIfIdentityMatches(
                    logicalSessionState,
                    runtimeIdentities,
                    normalizedSessionId,
                    revoked);
            }
            else
            {
                await AbortRevokedSessionAsync(
                    normalizedSessionId,
                    getActiveSessionId,
                    stopSession,
                    stopSessionRelay,
                    stopArtifactUploads,
                    stopWorkspaceMirrors,
                    cancelSessionDownloads,
                    getDownloadActiveSessionId,
                    setDownloadActiveSessionId).ConfigureAwait(false);
                ClearRuntimeSessionState(logicalSessionState, runtimeIdentities, normalizedSessionId);
            }
        }
        catch (Exception ex)
        {
            // The child may still be alive; keep its state until it exits.
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to abort revoked session {normalizedSessionId}: {ex.Message}");
        }
    }

    internal static async Task<bool> AbortRevokedSessionAsync(
        string? sessionId,
        Func<string> getActiveSessionId,
        Func<string, Func<string, CancellationToken, Task>?, CancellationToken, Task> stopSession,
        Func<string, Task> stopSessionRelay,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        Action<string?> cancelSessionDownloads,
        Func<string?> getDownloadActiveSessionId,
        Action<string?> setDownloadActiveSessionId)
    {
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return false;
        }

        RunnerLog.Warning(
            nameof(ServiceClientManager),
            $"Offloadr API no longer recognizes this runner for session '{normalizedSessionId}'. Aborting local session.");

        cancelSessionDownloads(normalizedSessionId);

        var activeDownloadSessionId = getDownloadActiveSessionId();
        if (string.Equals(activeDownloadSessionId, normalizedSessionId, StringComparison.Ordinal))
        {
            setDownloadActiveSessionId(null);
        }

        var activeSessionId = getActiveSessionId()?.Trim() ?? string.Empty;
        if (!string.Equals(activeSessionId, normalizedSessionId, StringComparison.Ordinal))
        {
            await StopSidecarsAndRelayAsync(normalizedSessionId, stopArtifactUploads, stopWorkspaceMirrors,
                stopSessionRelay, CancellationToken.None).ConfigureAwait(false);

            return false;
        }

        await StopSessionAndCleanupAsync(normalizedSessionId, stopSession, stopSessionRelay,
            stopArtifactUploads, stopWorkspaceMirrors, cancelSessionDownloads,
            getDownloadActiveSessionId, setDownloadActiveSessionId, CancellationToken.None).ConfigureAwait(false);

        return true;
    }

    internal static async Task StopSessionAndCleanupAsync(
        string? sessionId,
        Func<string, Func<string, CancellationToken, Task>?, CancellationToken, Task> stopSession,
        Func<string, Task> stopSessionRelay,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        Action<string?> cancelSessionDownloads,
        Func<string?> getDownloadActiveSessionId,
        Action<string?> setDownloadActiveSessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        cancelSessionDownloads(normalizedSessionId);

        if (string.Equals(getDownloadActiveSessionId(), normalizedSessionId, StringComparison.Ordinal))
        {
            setDownloadActiveSessionId(null);
        }

        Task? sidecarStop = null;
        Task StopSidecarsOnceAsync(string sid, CancellationToken token)
            => sidecarStop ??= StopArtifactAndWorkspaceMirrorsAsync(sid, stopArtifactUploads, stopWorkspaceMirrors, token);

        ExceptionDispatchInfo? stopFailure = null;
        try
        {
            await stopSession(normalizedSessionId, StopSidecarsOnceAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Session stop ended with {error.GetType().Name}.");
            stopFailure = ExceptionDispatchInfo.Capture(error);
        }
        try { await StopSidecarsOnceAsync(normalizedSessionId, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Sidecar cleanup ended with {error.GetType().Name}.");
            stopFailure ??= ExceptionDispatchInfo.Capture(error);
        }
        // A late native open may have published a descriptor lease during stop.
        cancelSessionDownloads(normalizedSessionId);
        try { await stopSessionRelay(normalizedSessionId).ConfigureAwait(false); }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Relay cleanup ended with {error.GetType().Name}.");
            stopFailure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("Session relay cleanup failed."));
        }

        // The child may still be alive: callers must not report it stopped or
        // drop the logical state that keeps it visible to the control plane.
        stopFailure?.Throw();
    }

    /// <summary>
    /// Cleans up after a runtime exited on its own. A replacement for the same session may
    /// already have adopted its identity, so only state and sidecars that still belong to
    /// the exited runtime are touched.
    /// </summary>
    internal static async Task HandleUnexpectedRuntimeExitAsync(
        string sessionId,
        RuntimeIdentity exitedRuntime,
        LogicalSessionState logicalSessionState,
        RuntimeIdentityRegistry runtimeIdentities,
        Func<string, CancellationToken, Task> cleanupSidecars,
        CancellationToken cancellationToken)
    {
        if (!exitedRuntime.IsValid)
        {
            // A runtime started without an identity has nothing newer to protect.
            ClearRuntimeSessionState(logicalSessionState, runtimeIdentities, sessionId);
            await cleanupSidecars(sessionId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var supersededInRegistry = runtimeIdentities.TryGet(sessionId, out var registered) &&
                                   !registered.SameRuntime(exitedRuntime);
        var supersededInSession = logicalSessionState.TryGetRuntime(sessionId, out var logical) &&
                                  !logical.SameRuntime(exitedRuntime);
        if (supersededInRegistry || supersededInSession)
        {
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Exited runtime {exitedRuntime.RuntimeId} of session '{sessionId}' was already replaced; leaving the newer runtime's state and sidecars.");
            return;
        }

        ClearRuntimeSessionStateIfIdentityMatches(
            logicalSessionState,
            runtimeIdentities,
            sessionId,
            exitedRuntime);
        await cleanupSidecars(sessionId, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task CleanupExitedSessionAsync(
        string? sessionId,
        Func<string, Task> stopSessionRelay,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        Action<string?> cancelSessionDownloads,
        Func<string?> getDownloadActiveSessionId,
        Action<string?> setDownloadActiveSessionId,
        CancellationToken cancellationToken)
    {
        var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalizedSessionId))
        {
            return;
        }

        cancelSessionDownloads(normalizedSessionId);

        if (string.Equals(getDownloadActiveSessionId(), normalizedSessionId, StringComparison.Ordinal))
        {
            setDownloadActiveSessionId(null);
        }

        await StopSidecarsAndRelayAsync(normalizedSessionId, stopArtifactUploads, stopWorkspaceMirrors,
            stopSessionRelay, cancellationToken).ConfigureAwait(false);
    }

    private static async Task StopSidecarsAndRelayAsync(string sessionId, Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors, Func<string, Task> stopSessionRelay, CancellationToken cancellationToken)
    {
        ExceptionDispatchInfo? failure = null;
        try { await StopArtifactAndWorkspaceMirrorsAsync(sessionId, stopArtifactUploads, stopWorkspaceMirrors, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
        try { await stopSessionRelay(sessionId).ConfigureAwait(false); }
        catch (Exception error)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Relay cleanup ended with {error.GetType().Name}.");
            failure ??= ExceptionDispatchInfo.Capture(new InvalidOperationException("Session relay cleanup failed."));
        }
        failure?.Throw();
    }

    internal static void ClearRuntimeSessionState(
        LogicalSessionState logicalSessionState,
        RuntimeIdentityRegistry runtimeIdentities,
        string? sessionId)
    {
        logicalSessionState.ClearIfMatches(sessionId);
        runtimeIdentities.Remove(sessionId);
    }

    internal static void ClearRuntimeSessionStateIfIdentityMatches(
        LogicalSessionState logicalSessionState,
        RuntimeIdentityRegistry runtimeIdentities,
        string? sessionId,
        RuntimeIdentity runtime)
    {
        logicalSessionState.ClearIfMatches(sessionId, runtime);
        runtimeIdentities.RemoveIfMatches(sessionId, runtime);
    }

    internal static async Task StopArtifactAndWorkspaceMirrorsAsync(
        string sessionId,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Invoke both owners even when the first factory fails. A slow-wait
        // threshold is diagnostic; account/key cleanup still joins originals.
        var artifacts = StopAsync(stopArtifactUploads, "Artifact uploader");
        var workspace = StopAsync(stopWorkspaceMirrors, "Workspace mirror");
        var results = await Task.WhenAll(artifacts, workspace).ConfigureAwait(false);
        if (results.Any(succeeded => !succeeded)) throw new InvalidOperationException("Session sidecar cleanup failed.");

        async Task<bool> StopAsync(Func<string, Task> factory, string kind)
        {
            try
            {
                var original = factory(sessionId);
                if (await Task.WhenAny(original, Task.Delay(MirrorStopTimeout)).ConfigureAwait(false) != original)
                    RunnerLog.Warning(nameof(ServiceClientManager), $"{kind} is still stopping; continuing to join original work.");
                await original.ConfigureAwait(false);
                return true;
            }
            catch (Exception error)
            {
                RunnerLog.Warning(nameof(ServiceClientManager), $"{kind} stop ended with {error.GetType().Name}.");
                return false;
            }
            finally { factory = null!; }
        }
    }

    internal static TimeSpan MirrorStopTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private static Task StopSessionAndCleanupAsync(
        string? sessionId,
        RuntimeCommandDependencies deps,
        CancellationToken cancellationToken)
        => StopSessionAndCleanupAsync(
            sessionId,
            deps.StopRuntime,
            deps.StopSessionRelay,
            deps.StopArtifactUploads,
            deps.StopWorkspaceMirrors,
            deps.CancelSessionDownloads,
            deps.GetDownloadActiveSessionId,
            deps.SetDownloadActiveSession,
            cancellationToken);

    /// <summary>
    /// A session's start sealing and the runtime of the start that set it. Every later start of
    /// the session keeps it, whatever its lifecycle generation.
    /// </summary>
    internal sealed record RetainedSealing(RuntimeIdentity StartRuntime, SessionSealing Sealing);

    /// <summary>
    /// Cleans up after a failed start of exactly <paramref name="failedRuntime"/>.
    /// A different runtime of the session that is still tracked is left running and
    /// remains the session's identity; a cleanup that could not stop the child keeps
    /// the state until the process exits.
    /// </summary>
    private static async Task CleanUpFailedStartupAsync(
        string sessionId,
        RuntimeIdentity failedRuntime,
        RuntimeCommandDependencies deps)
    {
        bool stopped;
        try
        {
            stopped = await StopRuntimeAndCleanupIfCurrentAsync(sessionId, failedRuntime, deps, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Cleanup after failed start of session {sessionId} did not complete; retaining its state.");
            return;
        }

        if (stopped)
        {
            ClearRuntimeSessionStateIfIdentityMatches(
                deps.LogicalSessionState,
                deps.RuntimeIdentities,
                sessionId,
                failedRuntime);
            // A failed first start's sealing goes with it: a later start that omits its own must
            // not inherit that recipient. A failed or refused relaunch keeps it: the session still
            // started sealed, and its next relaunch must be too.
            if (deps.WorkState.SessionSealing.TryGetValue(sessionId, out var retained) &&
                retained.StartRuntime.SameRuntime(failedRuntime))
            {
                ((ICollection<KeyValuePair<string, RetainedSealing>>)deps.WorkState.SessionSealing).Remove(new(sessionId, retained));
            }

            return;
        }

        var lifecycleGeneration = deps.RuntimeIdentities.LifecycleGeneration(sessionId);
        if (deps.GetTrackedRuntime(sessionId) is { } running &&
            deps.LogicalSessionState.ReplaceRuntimeIfMatches(sessionId, failedRuntime, running) &&
            deps.RuntimeIdentities.RemoveIfMatches(sessionId, failedRuntime))
        {
            deps.RuntimeIdentities.Set(sessionId, running, lifecycleGeneration);
        }
    }

    /// <summary>
    /// Handles Stop on the command stream. Physical cleanup is awaited; the
    /// acknowledgement is retried detached with the identical request, so a lost
    /// acknowledgement neither blocks later commands nor is dropped.
    /// </summary>
    internal static async Task HandleStopSessionCommandAsync(StopSessionCommand command, RuntimeCommandDependencies deps)
    {
        deps.Shutdown.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command.SessionId) || string.IsNullOrWhiteSpace(deps.InstanceId) ||
            !Guid.TryParse(command.CommandId, out var commandId) || commandId == Guid.Empty)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), "Ignoring StopSession with missing session or invalid command/process identity");
            return;
        }

        // Cleanup and acknowledgement retain the delivered identity even if the
        // caller changes its message while asynchronous cleanup is in progress.
        command = command.Clone();
        var ack = new AcknowledgeSessionStopRequest
        {
            SessionId = command.SessionId,
            User = command.User,
            CommandId = command.CommandId,
            InstanceId = deps.InstanceId.Trim(),
            UpToRuntimeOrder = command.UpToRuntimeOrder,
        };
        var acknowledgementKey = commandId.ToString("n");
        var work = deps.WorkState.Stops.GetOrAdd(commandId, _ => new StopCommandWork(ack));
        if (!work.Matches(ack))
        {
            RunnerLog.Warning(nameof(ServiceClientManager), "Ignoring StopSession that reuses a command identity with different session or process identity");
            return;
        }

        await work.Gate.WaitAsync(deps.Shutdown).ConfigureAwait(false);
        try
        {
            if (!work.Completed)
            {
                var inProgress = new StopInProgress(command.SessionId);
                try
                {
                    await ExecuteAndCountStopAsync(command, work, inProgress, deps).ConfigureAwait(false);
                }
                finally
                {
                    // Also on failure and shutdown: a start waiting for this Stop goes on.
                    deps.WorkState.EndStop(inProgress);
                }
            }

            if (work.Completed && (work.AcknowledgementTask is null || work.AcknowledgementTask.IsCompleted))
            {
                // One retry loop per command; redelivery after a finished loop
                // replays the same result without touching runtime state.
                work.AcknowledgementTask = deps.StartAcknowledgement(() => AcknowledgeStopAsync(work, acknowledgementKey, command, deps));
            }
        }
        finally
        {
            work.Gate.Release();
        }
    }

    /// <summary>Runs a Stop and fills in its acknowledgement from what it did.</summary>
    private static async Task ExecuteAndCountStopAsync(
        StopSessionCommand command,
        StopCommandWork work,
        StopInProgress inProgress,
        RuntimeCommandDependencies deps)
    {
        var outcome = await ExecuteStopSessionCommandAsync(command, inProgress, deps).ConfigureAwait(false);
        work.Completed = outcome != StopOutcome.NotCompleted;
        // Only the Stop that tore the session down performed its cleanup and reports what
        // the session lost. A superseded Stop did nothing: its saving evidence stays
        // unknown, and the count stays for the Stop that ends the newer runtime.
        if (outcome == StopOutcome.Superseded)
        {
            work.Acknowledgement.CleanupDisposition = RunnerCleanupDisposition.Superseded;
            work.Acknowledgement.UnsavedArtifactsUnknown = true;
        }
        else if (outcome == StopOutcome.Stopped)
        {
            var unsaved = deps.TakeUnsavedArtifacts?.Invoke(command.SessionId) ?? new UnsavedUploads(0, true);
            work.Acknowledgement.UnsavedArtifactCount = (uint)Math.Max(0, unsaved.Count);
            work.Acknowledgement.UnsavedArtifactsUnknown |= unsaved.Incomplete;
            work.Acknowledgement.CleanupDisposition = RunnerCleanupDisposition.Performed;
            work.CountTaken = true;
        }
    }

    /// <summary>
    /// A Stop of one session whose effect is not over yet: from the moment it records its cutoff
    /// until its cleanup, its sealing removal and its count of unsaved artifacts are done.
    /// </summary>
    internal sealed class StopInProgress(string sessionId)
    {
        public string SessionId { get; } = sessionId.Trim();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>This Stop joined with the session's earlier ones; set once registered.</summary>
        public Task? Joined { get; set; }
    }

    private enum StopOutcome
    {
        NotCompleted,
        Superseded,
        Stopped,
    }

    private static async Task<StopOutcome> ExecuteStopSessionCommandAsync(
        StopSessionCommand command,
        StopInProgress inProgress,
        RuntimeCommandDependencies deps)
    {
        deps.Shutdown.ThrowIfCancellationRequested();
        var cutoff = command.UpToRuntimeOrder;
        lock (deps.WorkState.AdmissionGate)
        {
            if (FindRuntimeNewerThanStop(command, deps) is not null)
            {
                // A delayed or redelivered Stop must not stop a runtime started after it.
                RunnerLog.Warning(
                    nameof(ServiceClientManager),
                    $"Ignoring stale stop for session {command.SessionId}; a newer runtime is current.");
                return StopOutcome.Superseded;
            }

            // Recorded before cleanup, so a start at or below the cutoff is refused from now on,
            // even when this cleanup fails and the Stop is sent again. A newer start admitted from
            // now on waits until this Stop's whole effect is over before it touches the runtime:
            // the cleanup stops whatever the session runs, and the count covers all of it.
            deps.LogicalSessionState.RecordStopCutoff(command.SessionId, cutoff);
            deps.WorkState.BeginStop(inProgress);
        }

        await deps.WorkState.Prompts.CancelAndWaitAsync(
            prompt => prompt.SessionId == command.SessionId).ConfigureAwait(false);

        var activeStartup = deps.WorkState.GetActiveStartup();
        if (activeStartup is not null &&
            string.Equals(activeStartup.SessionId, command.SessionId, StringComparison.Ordinal) &&
            activeStartup.RuntimeIdentity.RuntimeOrder <= cutoff)
        {
            await CancelTrackedWorkAsync(activeStartup).ConfigureAwait(false);
        }

        using var transientDrain = deps.TransientCancellation.BeginSessionDrain(command.SessionId);
        await transientDrain.Completion.ConfigureAwait(false);

        try
        {
            await StopSessionAndCleanupAsync(command.SessionId, deps, deps.Shutdown).ConfigureAwait(false);
            deps.Shutdown.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            // Keep the heartbeat and identity of a child that may still be alive,
            // and do not acknowledge: the control plane retains the Stop.
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Stop of session {command.SessionId} did not complete; retaining its state.");
            return StopOutcome.NotCompleted;
        }

        deps.LogicalSessionState.ClearAfterStop(command.SessionId, cutoff);
        if (deps.RuntimeIdentities.TryGet(command.SessionId, out var registered) && registered.RuntimeOrder <= cutoff)
        {
            deps.RuntimeIdentities.RemoveIfMatches(command.SessionId, registered);
        }

        deps.WorkState.SessionSealing.TryRemove(command.SessionId, out _);
        return StopOutcome.Stopped;
    }

    /// <summary>
    /// The one comparison a Stop makes: a runtime, child or startup of its session with an
    /// order above the Stop's cutoff, if any. Such a Stop is superseded and does nothing.
    /// </summary>
    private static RuntimeIdentity? FindRuntimeNewerThanStop(StopSessionCommand command, RuntimeCommandDependencies deps)
    {
        var cutoff = command.UpToRuntimeOrder;
        if (deps.LogicalSessionState.TryGetRuntime(command.SessionId, out var logical) && logical.RuntimeOrder > cutoff)
        {
            return logical;
        }

        if (deps.GetTrackedRuntime(command.SessionId) is { } tracked && tracked.RuntimeOrder > cutoff)
        {
            return tracked;
        }

        return deps.WorkState.GetActiveStartup() is { } startup &&
               string.Equals(startup.SessionId, command.SessionId, StringComparison.Ordinal) &&
               startup.RuntimeIdentity.RuntimeOrder > cutoff
            ? startup.RuntimeIdentity
            : null;
    }

    /// <summary>
    /// Handles one delivery of a start. Older work is refused. A repeated start of the runtime
    /// adopted joins its startup in flight, or is answered with its recorded acknowledgement.
    /// Newer work replaces what runs: the startup in flight is cancelled, the session's older
    /// child stopped or another session pre-empted, and the runtime launched and acknowledged.
    /// </summary>
    internal static Task HandleStartRuntimeCommand(StartRuntimeCommand command, RuntimeCommandDependencies deps)
    {
        // The startup and the acknowledgement keep the delivered command, even if the caller
        // changes its message meanwhile.
        command = command.Clone();
        var runtime = RuntimeIdentity.From(command.Runtime);
        var ack = new AcknowledgeRuntimeStartRequest
        {
            InstanceId = deps.InstanceId.Trim(),
            SessionId = command.SessionId,
            User = command.User,
            Runtime = command.Runtime?.Clone(),
        };

        // Classifying the start and publishing its startup are one step against Stop's fence,
        // so a Stop either sees this startup or has already fenced it.
        Task<StartupOutcome>? outcome;
        lock (deps.WorkState.AdmissionGate)
        {
            outcome = deps.LogicalSessionState.AdmitStart(command.SessionId, runtime) switch
            {
                StartAdmission.Same => FindAdoptedStartup(command, runtime, deps),
                StartAdmission.Newer => PublishStartup(command, runtime, deps.WorkState.StopsInProgress(command.SessionId), deps),
                _ => null,
            };
        }

        if (outcome is null)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Rejecting stale start for session {command.SessionId}.");
            outcome = Task.FromResult(new StartupOutcome(false, StaleRuntimeCommandMessage));
        }

        return AcknowledgeStartAsync(outcome, ack, deps);
    }

    private static async Task AcknowledgeStartAsync(
        Task<StartupOutcome> outcome,
        AcknowledgeRuntimeStartRequest ack,
        RuntimeCommandDependencies deps)
    {
        var result = await outcome.ConfigureAwait(false);
        ack.Ready = result.Ready;
        ack.Message = result.Message;
        ack.Sealing = result.Ready ? result.Sealing?.Clone() : null;
        await AcknowledgeRuntimeCommandWithRetryAsync(
            deps.Sink.AckRuntimeStartAsync,
            ack,
            ack.SessionId,
            deps.Shutdown).ConfigureAwait(false);
    }

    /// <summary>
    /// The outcome a repeated start of the adopted runtime shares: its startup in flight, or how
    /// it ended. One asking for other sealing is refused instead: that startup never checked it,
    /// and it may be running on disk. Null when neither is known any more.
    /// </summary>
    private static Task<StartupOutcome>? FindAdoptedStartup(
        StartRuntimeCommand command,
        RuntimeIdentity runtime,
        RuntimeCommandDependencies deps)
    {
        var (startup, recorded, _) = deps.WorkState.ReadStartupState(command.SessionId, runtime);
        if (startup is { Outcome: { } inProgress } &&
            string.Equals(startup.SessionId, command.SessionId, StringComparison.Ordinal) &&
            startup.RuntimeIdentity.SameRuntime(runtime))
        {
            if (!Equals(startup.Sealing, command.Sealing))
            {
                RunnerLog.Warning(
                    nameof(ServiceClientManager),
                    $"A repeated start of session {command.SessionId} asks for other sealing than the one in progress; refusing it.");
                return Task.FromResult(new StartupOutcome(false, OtherSealingMessage));
            }

            RunnerLog.Info(nameof(ServiceClientManager), $"The start of session {command.SessionId} is already in progress; joining it.");
            return inProgress;
        }

        if (recorded is null)
        {
            return null;
        }

        if (!Equals(recorded.Requested, command.Sealing))
        {
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"A repeated start of session {command.SessionId} asks for other sealing than it ran with; refusing it.");
            return Task.FromResult(new StartupOutcome(false, OtherSealingMessage));
        }

        RunnerLog.Info(nameof(ServiceClientManager), $"The start of session {command.SessionId} already finished; sending its acknowledgement again.");
        return Task.FromResult(recorded.Outcome);
    }

    /// <summary>
    /// Publishes a newer start's startup in the single startup slot and starts it. The startup
    /// it supersedes is cancelled and awaited before this one touches the runtime, so two
    /// startups never overlap. It then waits for <paramref name="stopsInProgress"/>, the Stops of
    /// its session that were running when it was admitted: they stop whatever the session runs
    /// and count all of its losses, so it touches nothing before they end. A Stop admitted later
    /// sees this startup and is superseded or cancels it, so the wait never closes a cycle.
    /// The tracked task covers only the physical work; the acknowledgement retry runs after it,
    /// detached, so Stop never waits for it.
    /// </summary>
    private static Task<StartupOutcome> PublishStartup(
        StartRuntimeCommand command,
        RuntimeIdentity runtime,
        Task? stopsInProgress,
        RuntimeCommandDependencies deps)
    {
        var tracked = new TrackedCommandWork(deps.Shutdown)
        {
            SessionId = command.SessionId,
            RuntimeIdentity = runtime,
            Sealing = command.Sealing?.Clone(),
        };

        // Publish a joinable task before it starts, so a Stop or a repeated start can never
        // observe the slot without the work that owns it.
        TrackedCommandWork? previous = null;
        var starter = new Task<Task<StartupOutcome>>(RunPhysicalAsync);
        var physical = starter.Unwrap();
        tracked.Task = physical;
        tracked.Outcome = physical;
        previous = deps.WorkState.ReplaceActiveStartup(tracked);
        starter.Start(TaskScheduler.Default);
        return physical;

        async Task<StartupOutcome> RunPhysicalAsync()
        {
            try
            {
                await CancelTrackedWorkAsync(previous).ConfigureAwait(false);
                StartupOutcome result;
                if (await WaitForStopsAsync(stopsInProgress, tracked.Token).ConfigureAwait(false))
                {
                    result = await StartRuntimeAsync(command, runtime, deps, tracked.Token).ConfigureAwait(false);
                }
                else
                {
                    // It touched nothing, but it may have replaced another session's startup
                    // whose child was already gone: that session must not stay held.
                    result = new StartupOutcome(false, "The start was cancelled while a Stop of its session finished.");
                    ClearSupersededSession(command.SessionId, deps);
                }

                // Recorded while this startup still holds the slot, so a repeated start sees
                // either this startup in progress or its outcome.
                deps.WorkState.RecordStartupOutcome(command.SessionId, runtime, result, deps.GetTrackedProcess, command.Sealing);
                return result;
            }
            finally
            {
                try { await tracked.CompleteAsync().ConfigureAwait(false); }
                finally { deps.WorkState.ClearActiveStartup(tracked); }
            }
        }
    }

    /// <summary>
    /// Waits for the Stops a start was admitted behind. False when the start was cancelled
    /// meanwhile, by a later Stop, a newer start or shutdown; it has touched nothing then.
    /// </summary>
    private static async Task<bool> WaitForStopsAsync(Task? stopsInProgress, CancellationToken cancellationToken)
    {
        if (stopsInProgress is null)
        {
            return true;
        }

        try
        {
            await stopsInProgress.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The physical work of a newer start, ending in the outcome its acknowledgement reports.</summary>
    private static async Task<StartupOutcome> StartRuntimeAsync(
        StartRuntimeCommand command,
        RuntimeIdentity runtime,
        RuntimeCommandDependencies deps,
        CancellationToken cancellationToken)
    {
        var sessionId = command.SessionId;
        // The session's sealing from its earlier start: a relaunch carries it, and may not weaken
        // it. It belongs to the session: the lifecycle generation only matches GPU power commands.
        var retained = deps.WorkState.SessionSealing.TryGetValue(sessionId, out var kept) ? kept : null;
        var launch = command.Clone();
        launch.Sealing = command.Sealing?.Clone() ?? retained?.Sealing.Clone();
        var weakensSealing = retained is not null && command.Sealing is { } requested && WeakensSealing(retained.Sealing, requested);
        try
        {
            // Prompts of every other runtime end now: other sessions', and this session's older ones.
            await deps.WorkState.Prompts.CancelAndWaitAsync(
                prompt => !PromptTargetsRuntime(prompt, sessionId, runtime)).ConfigureAwait(false);

            await StopReplacedRuntimeAsync(sessionId, runtime, deps, cancellationToken).ConfigureAwait(false);

            // This wait belongs to cancellable startup work, never the
            // command stream: Stop must still preempt a queued replacement.
            await deps.ActivateSession(() =>
            {
                if (!deps.LogicalSessionState.TryAdoptRuntime(sessionId, runtime))
                {
                    throw new InvalidOperationException(StaleRuntimeCommandMessage);
                }

                deps.RuntimeIdentities.Set(sessionId, runtime, command.LifecycleGeneration);
                // A first start's sealing stands for the session; a relaunch replaces it only
                // once it ran with it.
                if (retained is null && command.Sealing is { } sealing)
                {
                    deps.WorkState.SessionSealing[sessionId] = new RetainedSealing(runtime, sealing.Clone());
                }
            }, cancellationToken).ConfigureAwait(false);

            var preemptedSessionId = await PreemptActiveSessionIfNeededAsync(
                sessionId,
                deps.GetActiveRuntimeSessionId,
                deps.StopRuntime,
                deps.StopSessionRelay,
                deps.StopArtifactUploads,
                deps.StopWorkspaceMirrors,
                deps.CancelSessionDownloads,
                deps.GetDownloadActiveSessionId,
                deps.SetDownloadActiveSession,
                cancellationToken).ConfigureAwait(false);
            deps.RuntimeIdentities.Remove(preemptedSessionId);

            if (weakensSealing)
            {
                throw new InvalidOperationException("The session started sealed; a relaunch cannot run it with less sealing.");
            }

            await deps.StartRuntimeAndWaitForReady(launch, cancellationToken).ConfigureAwait(false);
            if (retained is not null && command.Sealing is { } learned)
            {
                // What this relaunch ran with stands for the session from now on: it is never
                // weaker than what it replaces, and may be stronger. The first start's runtime
                // stays, so only that start failing drops it.
                deps.WorkState.SessionSealing.AddOrUpdate(
                    sessionId,
                    new RetainedSealing(runtime, learned.Clone()),
                    (_, existing) => existing with { Sealing = learned.Clone() });
            }

            return new StartupOutcome(
                true,
                IsComfyRuntime(command.EditorRuntimeKind) ? "ComfyUI ready" : "GPU runtime ready",
                deps.GetSessionSealing?.Invoke(sessionId));
        }
        catch (SessionAlreadyRunningException ex)
        {
            // That runtime runs on, as it was admitted: only this command is refused.
            LogStartFailure(launch, deps, ex, $"Refused a start of session {sessionId}");
            return new StartupOutcome(false, ex.Message);
        }
        catch (Exception ex)
        {
            LogStartFailure(launch, deps, ex, $"Failed to start session {sessionId}");
            // Clean up only this start's runtime, never a valid runtime of another
            // identity that happens to share the session id.
            await CleanUpFailedStartupAsync(sessionId, runtime, deps).ConfigureAwait(false);
            ClearSupersededSession(sessionId, deps);
            return new StartupOutcome(false, ex.Message);
        }
    }

    /// <summary>
    /// After a start of <paramref name="failedSessionId"/> failed or was cancelled, drops another
    /// session the runner still holds with no child: that start superseded it on the control
    /// plane and its child is gone, so the heartbeat must not keep reporting it. The failed
    /// session itself is kept, as is one whose child still runs or whose own startup is running.
    /// </summary>
    private static void ClearSupersededSession(string failedSessionId, RuntimeCommandDependencies deps)
    {
        lock (deps.WorkState.AdmissionGate)
        {
            var held = deps.LogicalSessionState.GetActiveSessionId();
            if (held.Length == 0 ||
                string.Equals(held, failedSessionId?.Trim(), StringComparison.Ordinal) ||
                deps.GetTrackedRuntime(held) is not null ||
                deps.WorkState.GetActiveStartup() is { } startup && string.Equals(startup.SessionId?.Trim(), held, StringComparison.Ordinal))
            {
                return;
            }

            RunnerLog.Info(nameof(ServiceClientManager), $"Dropping session {held}: a newer start superseded it and it has no child.");
            if (deps.LogicalSessionState.TryGetRuntime(held, out var runtime))
            {
                ClearRuntimeSessionStateIfIdentityMatches(deps.LogicalSessionState, deps.RuntimeIdentities, held, runtime);
            }
            else
            {
                ClearRuntimeSessionState(deps.LogicalSessionState, deps.RuntimeIdentities, held);
            }
        }
    }

    /// <summary>
    /// A relaunch keeps the logical session: it never runs it with less sealing than it started
    /// with, neither unsealed nor encrypting its uploads to another recipient, or to none.
    /// </summary>
    private static bool WeakensSealing(SessionSealing started, SessionSealing relaunch)
        => (started.Required && !relaunch.Required) ||
           (started.OutputRecipient is not null && !started.OutputRecipient.Equals(relaunch.OutputRecipient));

    /// <summary>
    /// Before a relaunch, stops the session's older runtime as a restart's quiesce did: its
    /// relayed requests are cancelled and drained, then its child is stopped and the session
    /// cleaned up. Nothing happens when the session runs no older runtime here.
    /// </summary>
    private static async Task StopReplacedRuntimeAsync(
        string sessionId,
        RuntimeIdentity replacement,
        RuntimeCommandDependencies deps,
        CancellationToken cancellationToken)
    {
        var tracked = deps.GetTrackedRuntime(sessionId) is { } child && !child.SameRuntime(replacement) ? child : (RuntimeIdentity?)null;
        var logical = deps.LogicalSessionState.TryGetRuntime(sessionId, out var current) && !current.SameRuntime(replacement)
            ? current
            : (RuntimeIdentity?)null;
        if (tracked is null && logical is null)
        {
            return;
        }

        RunnerLog.Info(nameof(ServiceClientManager), $"Stopping the older runtime of session {sessionId} before its relaunch.");
        using (var transientDrain = deps.TransientCancellation.BeginSessionDrain(sessionId))
        {
            await transientDrain.Completion.ConfigureAwait(false);
            await StopSessionAndCleanupAsync(sessionId, deps, cancellationToken).ConfigureAwait(false);
        }

        foreach (var stopped in new[] { tracked, logical })
        {
            if (stopped is { } runtime)
            {
                deps.RuntimeIdentities.RemoveIfMatches(sessionId, runtime);
            }
        }
    }

    /// <summary>
    /// Logs why a start failed. For a sealed one only the exception's type is logged: its
    /// message can name session files (a workspace seed's entries, say), and the console reaches
    /// the host's disk. The acknowledgement still carries the message to the control plane.
    /// </summary>
    private static void LogStartFailure(StartRuntimeCommand start, RuntimeCommandDependencies deps, Exception ex, string context)
    {
        if (start.Sealing?.Required == true || deps.SealsStart?.Invoke(start) == true)
        {
            RunnerLog.Error(nameof(ServiceClientManager), $"{context}: {ex.GetType().Name} (details withheld for a sealed session).");
            return;
        }

        RunnerLog.Error(nameof(ServiceClientManager), ex, $"{context}: {ex.Message}");
    }

    private static bool PromptTargetsRuntime(PromptWorkIdentity prompt, string sessionId, RuntimeIdentity runtimeIdentity)
        => string.Equals(prompt.SessionId, sessionId, StringComparison.Ordinal) &&
           prompt.Runtime.SameRuntime(runtimeIdentity);

    /// <summary>
    /// Stops and cleans up a session only while its tracked child is the expected
    /// runtime. Returns false, touching nothing, when another runtime is tracked.
    /// </summary>
    private static async Task<bool> StopRuntimeAndCleanupIfCurrentAsync(
        string sessionId,
        RuntimeIdentity expected,
        RuntimeCommandDependencies deps,
        CancellationToken cancellationToken)
    {
        if (deps.GetTrackedRuntime(sessionId) is { } tracked && !tracked.SameRuntime(expected))
        {
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Leaving session {sessionId} running: its runtime is not the identity being cleaned up.");
            return false;
        }

        var stopped = true;
        await StopSessionAndCleanupAsync(
            sessionId,
            async (sid, beforeCleanup, token) =>
                stopped = await deps.StopRuntimeIfIdentityMatches(sid, expected, beforeCleanup, token).ConfigureAwait(false),
            deps.StopSessionRelay,
            deps.StopArtifactUploads,
            deps.StopWorkspaceMirrors,
            deps.CancelSessionDownloads,
            deps.GetDownloadActiveSessionId,
            deps.SetDownloadActiveSession,
            cancellationToken).ConfigureAwait(false);
        return stopped;
    }

    private const string StaleRuntimeCommandMessage = "The command targets a superseded runtime or another runner.";

    internal static bool RunnerIdMatches(string? commandRunnerId, string runnerId)
        => Guid.TryParse(commandRunnerId, out var commandRunner) && Guid.TryParse(runnerId, out var runner)
            ? commandRunner == runner
            : !string.IsNullOrWhiteSpace(commandRunnerId) &&
              string.Equals(commandRunnerId.Trim(), runnerId?.Trim(), StringComparison.Ordinal);

    internal const string OtherSealingMessage = "This runtime is already starting with other sealing than this command asks for.";

    private static bool IsComfyRuntime(string? runtimeKind)
        => string.IsNullOrWhiteSpace(runtimeKind) ||
           string.Equals(runtimeKind.Trim(), "comfyui", StringComparison.OrdinalIgnoreCase);

    private static async Task StartSessionAndWaitForReadyAsync(
        StartRuntimeCommand command,
        SessionProcessManager sessionManager,
        ArtifactUploadService artifactUploadService,
        WorkspaceMirrorService workspaceMirrorService,
        ModelDownloadService downloadService,
        Func<SessionProcessManager.SessionPaths?, IEnumerable<ModelDownloadRequest>, bool, CancellationToken, Task>? localModelProjectionReconciler,
        CancellationToken cancellationToken)
    {
        downloadService.SetActiveSession(command.SessionId);
        await sessionManager.StartSessionAsync(
            command,
            async (sessionPaths, token) =>
            {
                var initialModels = MaterializeModelDownloadPathsForSession(
                    command.InitialModels,
                    command.EditorRuntimeKind,
                    sessionPaths);
                if (localModelProjectionReconciler is not null)
                {
                    await localModelProjectionReconciler(sessionPaths, initialModels, true, token).ConfigureAwait(false);
                }

                var remoteInitialModels = FilterRemoteDownloadRequests(initialModels);

                await artifactUploadService.StartSessionAsync(command.SessionId, sessionPaths, token).ConfigureAwait(false);
                await workspaceMirrorService
                    .PrepareSessionAsync(
                        command.SessionId,
                        sessionPaths,
                        command.EditorSid,
                        command.EditorRuntimeKind,
                        command.InitialWorkspaceFiles,
                        token)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(command.EditorSid) || command.InitialArtifacts.Count > 0)
                {
                    await artifactUploadService
                        .SeedSessionAsync(
                            command.SessionId,
                            command.EditorSid,
                            command.User,
                            command.InitialArtifacts,
                            token)
                        .ConfigureAwait(false);
                }

                downloadService.SeedDownloads(command.SessionId, remoteInitialModels);
            },
            (_, _) =>
            {
                artifactUploadService.RegisterInputSeeds(command.SessionId);
                return Task.CompletedTask;
            },
            cancellationToken).ConfigureAwait(false);
        await sessionManager.WaitForSessionReadyAsync(command.SessionId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task CancelTrackedWorkAsync(TrackedCommandWork? work)
    {
        if (work is null)
        {
            return;
        }

        var cancellation = work.CancelAsync();
        try
        {
            if (work.Task is { } original) await original.ConfigureAwait(false);
        }
        catch
        {
            // Startup failures belong to the physical startup's outcome path.
        }
        try { await cancellation.ConfigureAwait(false); }
        catch (Exception error)
        {
            // The original root and callbacks have ended. A callback failure
            // must not skip runtime cleanup or copy its message into an ACK.
            RunnerLog.Warning(nameof(ServiceClientManager), $"Startup cancellation ended with {error.GetType().Name}.");
        }
    }

    /// <summary>
    /// Routes one delivery of a transient editor request. The first delivery
    /// executes; a redelivery while it runs joins it and is acknowledged with the
    /// same result under its own delivery id; a redelivery after completion
    /// replays the retained result without executing again.
    /// </summary>
    internal static async Task DispatchTransientEditorRuntimeRequest(
        RelayEditorRuntimeRequestCommand request,
        string runnerId,
        TransientRequestDeliveries deliveries,
        TransientCancellationRegistry cancellation,
        Func<RelayEditorRuntimeRequestCommand, CancellationToken, Task<AcknowledgeEditorRuntimeRequestRequest>> execute,
        Func<AcknowledgeEditorRuntimeRequestRequest, CancellationToken, Task> acknowledge,
        CancellationToken acknowledgementCancellationToken)
    {
        // Capture the command used for both identity admission and execution.
        request = request.Clone();

        switch (deliveries.Admit(request, runnerId, out var replay))
        {
            case TransientRequestAdmission.Replay:
                await AcknowledgeTransientRequestWithRetryAsync(acknowledge, replay!, acknowledgementCancellationToken).ConfigureAwait(false);
                return;
            case TransientRequestAdmission.Joined:
                return;
            case TransientRequestAdmission.IdentityMismatch:
                await AcknowledgeTransientRequestWithRetryAsync(
                    acknowledge,
                    TransientRequestFailure(request, 409, "The request ID was already used for a different command."),
                    acknowledgementCancellationToken).ConfigureAwait(false);
                return;
            case TransientRequestAdmission.AlreadyExecuted:
                await AcknowledgeTransientRequestWithRetryAsync(
                    acknowledge,
                    TransientRequestFailure(request, 409, "The request already ran on this runner; its result is no longer retained."),
                    acknowledgementCancellationToken).ConfigureAwait(false);
                return;
            case TransientRequestAdmission.WrongRunner:
                await AcknowledgeTransientRequestWithRetryAsync(
                    acknowledge,
                    TransientRequestFailure(request, 403, StaleRuntimeCommandMessage),
                    acknowledgementCancellationToken).ConfigureAwait(false);
                return;
            case TransientRequestAdmission.Expired:
                // Nothing runs for an expired request, including the work done before execution.
                await AcknowledgeTransientRequestWithRetryAsync(
                    acknowledge,
                    TransientRequestFailure(request, 504, "The request has no deadline or expired before it could run."),
                    acknowledgementCancellationToken).ConfigureAwait(false);
                return;
            case TransientRequestAdmission.Saturated:
                await AcknowledgeTransientRequestWithRetryAsync(
                    acknowledge,
                    TransientRequestFailure(request, 503, "Too many recent requests to admit another safely."),
                    acknowledgementCancellationToken).ConfigureAwait(false);
                return;
        }

        AcknowledgeEditorRuntimeRequestRequest response;
        try
        {
            await using var lease = cancellation.Acquire(request.SessionId, RuntimeIdentity.From(request.Runtime));
            response = await execute(request, lease.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Transient editor request {request.RequestId} failed: {ex.Message}");
            response = new AcknowledgeEditorRuntimeRequestRequest
            {
                RequestId = request.RequestId,
                DeliveryId = request.DeliveryId,
                RunnerId = request.RunnerId,
                SessionId = request.SessionId,
                Runtime = request.Runtime?.Clone(),
                Kind = request.Kind,
                TransportSucceeded = false,
                StatusCode = 502,
                ErrorMessage = ex.Message,
            };
        }

        await Task.WhenAll(deliveries.Complete(response).Select(ack =>
            AcknowledgeTransientRequestWithRetryAsync(acknowledge, ack, acknowledgementCancellationToken))).ConfigureAwait(false);
    }

    private static AcknowledgeEditorRuntimeRequestRequest TransientRequestFailure(
        RelayEditorRuntimeRequestCommand request,
        int statusCode,
        string message)
        => new()
        {
            RequestId = request.RequestId,
            DeliveryId = request.DeliveryId,
            RunnerId = request.RunnerId,
            SessionId = request.SessionId,
            Runtime = request.Runtime?.Clone(),
            Kind = request.Kind,
            TransportSucceeded = false,
            StatusCode = statusCode,
            ErrorMessage = message,
        };

    internal static async Task<AcknowledgeEditorRuntimeRequestRequest> ExecuteTransientEditorRuntimeRequestAsync(
        RelayEditorRuntimeRequestCommand command,
        string runnerId,
        SessionProcessManager sessionManager,
        ComfySessionEventRelay sessionEventRelay,
        SemaphoreSlim ordinaryControlGate,
        SemaphoreSlim readGate,
        Action beforeSend,
        CancellationToken cancellationToken,
        Func<AcknowledgeEditorRuntimeRequestRequest, CancellationToken, Task>? afterSend = null)
    {
        var response = new AcknowledgeEditorRuntimeRequestRequest
        {
            RequestId = command.RequestId,
            DeliveryId = command.DeliveryId,
            RunnerId = runnerId,
            SessionId = command.SessionId,
            Runtime = command.Runtime?.Clone(),
            Kind = command.Kind
        };
        SemaphoreSlim? gate = command.Kind switch
        {
            EditorRuntimeRequestKind.Read => readGate,
            EditorRuntimeRequestKind.Control when !command.Urgent => ordinaryControlGate,
            _ => null
        };
        var gateAcquired = false;

        try
        {
            if (gate is not null)
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
            }

            // The deadline can pass while the request waits behind others of its kind.
            if (TransientRequestDeliveries.IsExpired(command, DateTime.UtcNow))
            {
                response.TransportSucceeded = false;
                response.StatusCode = 504;
                response.ErrorMessage = "The request expired before it could run.";
                return response;
            }

            // Side effects such as cancelling prompts for an interrupt happen only once the
            // request has its turn and is still within its deadline.
            beforeSend();

            if (command.Kind == EditorRuntimeRequestKind.ClientMessage)
            {
                await sessionEventRelay.SendClientMessageAsync(
                    command.SessionId,
                    command.EditorSid,
                    command.ClientId,
                    RuntimeIdentity.From(command.Runtime),
                    command.ClientMessage.ToByteArray(),
                    cancellationToken).ConfigureAwait(false);
                response.TransportSucceeded = true;
                response.StatusCode = 204;
            }
            else
            {
                var result = await sessionManager.RelayEditorRuntimeRequestAsync(command, cancellationToken).ConfigureAwait(false);
                response.TransportSucceeded = true;
                response.StatusCode = result.StatusCode;
                response.Body = ByteString.CopyFrom(result.Body);
                response.ContentType = result.ContentType;
            }

            if (afterSend is not null)
            {
                // Follow-up effects never change the relayed result.
                try
                {
                    await afterSend(response, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RunnerLog.Warning(nameof(ServiceClientManager), $"Transient editor request {command.RequestId} follow-up failed ({ex.GetType().Name}).");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            response.TransportSucceeded = false;
            response.StatusCode = 499;
            response.ErrorMessage = "Editor runtime relay cancelled.";
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Transient editor request {command.RequestId} failed: {ex.Message}");
            response.TransportSucceeded = false;
            response.StatusCode = 502;
            response.ErrorMessage = ex.Message;
        }
        finally
        {
            if (gateAcquired)
            {
                gate!.Release();
            }
        }

        return response;
    }

    internal static async Task<bool> AcknowledgeTransientRequestWithRetryAsync(
        Func<AcknowledgeEditorRuntimeRequestRequest, CancellationToken, Task> acknowledge,
        AcknowledgeEditorRuntimeRequestRequest response,
        CancellationToken cancellationToken,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        var retryDelay = initialRetryDelay ?? TimeSpan.FromMilliseconds(250);
        var maxRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(5);
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await acknowledge(response, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (IsRetryableTransientAcknowledgementFailure(ex))
            {
                if (attempt == 1 || attempt % 12 == 0)
                {
                    RunnerLog.Warning(
                        nameof(ServiceClientManager),
                        $"Transient editor acknowledgement {response.RequestId} failed; retrying: {ex.Message}");
                }

                try
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, maxRetryDelay.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                RunnerLog.Error(
                    nameof(ServiceClientManager),
                    ex,
                    $"Transient editor acknowledgement {response.RequestId} was rejected: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Acknowledges a Stop, then, once the control plane has its count of lost artifacts, drops
    /// the session's durable notes: until then a restart must still report the count unknown.
    /// The notes go at most once per Stop, and only while every runtime the session adopted here is
    /// at or below the Stop's cutoff: a replay or a late retry must not drop the notes of a newer
    /// runtime, even one that has since exited or been pre-empted, whose losses this Stop's count
    /// does not cover.
    /// </summary>
    private static async Task AcknowledgeStopAsync(StopCommandWork work, string acknowledgementKey, StopSessionCommand command, RuntimeCommandDependencies deps)
    {
        if (!await AcknowledgeRuntimeCommandWithRetryAsync(deps.Sink.AckStopSessionAsync, work.Acknowledgement, acknowledgementKey, deps.Shutdown).ConfigureAwait(false) ||
            !work.CountTaken || !work.TryClaimForgetNotes())
        {
            return;
        }

        lock (deps.WorkState.AdmissionGate)
        {
            if (FindRuntimeNewerThanStop(command, deps) is not null ||
                deps.LogicalSessionState.AdoptedAbove(command.SessionId, command.UpToRuntimeOrder))
            {
                RunnerLog.Info(nameof(ServiceClientManager), $"Keeping the sealed notes of session {command.SessionId}: a newer runtime started since its Stop.");
                return;
            }

            deps.ForgetSealedLifecycle?.Invoke(command.SessionId);
        }
    }

    internal static async Task<bool> AcknowledgeRuntimeCommandWithRetryAsync<TRequest>(
        Func<TRequest, CancellationToken, Task> acknowledge,
        TRequest response,
        string commandId,
        CancellationToken cancellationToken,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null)
    {
        var retryDelay = initialRetryDelay ?? TimeSpan.FromMilliseconds(250);
        var maxRetryDelay = maximumRetryDelay ?? TimeSpan.FromSeconds(5);
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            attempt++;
            try
            {
                await acknowledge(response, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception ex) when (IsRetryableTransientAcknowledgementFailure(ex))
            {
                if (attempt == 1 || attempt % 12 == 0)
                {
                    RunnerLog.Warning(
                        nameof(ServiceClientManager),
                        $"Runtime command acknowledgement {commandId} failed; retrying: {ex.Message}");
                }

                try
                {
                    await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }

                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, maxRetryDelay.TotalMilliseconds));
            }
            catch (Exception ex)
            {
                RunnerLog.Error(
                    nameof(ServiceClientManager),
                    ex,
                    $"Runtime command acknowledgement {commandId} was rejected: {ex.Message}");
                return false;
            }
        }

        return false;
    }

    private static bool IsRetryableTransientAcknowledgementFailure(Exception exception)
        => exception is HttpRequestException or IOException or TimeoutException ||
           exception is RpcException
           {
               StatusCode: StatusCode.Cancelled or
                   StatusCode.Unknown or
                   StatusCode.DeadlineExceeded or
                   StatusCode.ResourceExhausted or
                   StatusCode.Aborted or
                   StatusCode.Internal or
                   StatusCode.Unavailable or
                   StatusCode.DataLoss
           };

    // How long a sent grant may still settle after its prompt was interrupted or stopped.
    internal static readonly TimeSpan GrantSettleTimeout = TimeSpan.FromSeconds(30);

    // Asks for the grant, retrying a lost reply with the same request: a retry from this
    // instance is answered GRANTED again, so it cannot run the handoff twice. Null when the
    // grant was refused, or was not answered before its lifetime ended.
    private static async Task<GrantPromptExecutionResponse?> RequestGrantAsync(
        GrantPromptExecutionRequest grant,
        Func<GrantPromptExecutionRequest, CancellationToken, Task<GrantPromptExecutionResponse>> grantExecution,
        string handoffId,
        CancellationToken lifetime)
    {
        var attempt = 0;
        var retryDelay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            attempt++;
            try
            {
                return await grantExecution(grant, lifetime).ConfigureAwait(false);
            }
            // Cancelled, as an exception or a gRPC status: the grant's answer is not known.
            catch (Exception) when (lifetime.IsCancellationRequested) { return null; }
            catch (Exception ex) when (IsRetryableTransientAcknowledgementFailure(ex))
            {
                if (attempt == 1 || attempt % 12 == 0)
                    RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt grant {handoffId} unavailable ({ex.GetType().Name}); retrying the same grant.");
                try { await Task.Delay(retryDelay, lifetime).ConfigureAwait(false); }
                catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return null; }
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 5000));
            }
            catch (Exception ex)
            {
                RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt grant {handoffId} rejected ({ex.GetType().Name}); dropping the delivery.");
                return null;
            }
        }
    }

    // The grant of an interrupted or stopped handoff, settling after its work ended. The
    // handoff is already remembered as granted. If the platform granted it, it is reported
    // not executed; if no answer comes, the platform releases it on Stop, a new runner
    // process or its expiry.
    private static async Task SettleInterruptedGrantAsync(
        Task<GrantPromptExecutionResponse?> grantRequest,
        CancellationTokenSource grantLifetime,
        AcknowledgePromptResultRequest ack,
        Func<AcknowledgePromptResultRequest, CancellationToken, Task> ackSubmitPrompt,
        CancellationToken hostShutdown)
    {
        try
        {
            var permission = await grantRequest.ConfigureAwait(false);
            if (permission?.Outcome == PromptGrantOutcome.Granted)
                await AcknowledgeNotExecutedAsync(ack, ackSubmitPrompt).ConfigureAwait(false);
            else if (permission is null && grantLifetime.IsCancellationRequested && !hostShutdown.IsCancellationRequested)
                RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt grant {ack.HandoffId} was not answered after the prompt was interrupted; it will not run.");
        }
        finally { grantLifetime.Dispose(); }
    }

    private static async Task AcknowledgeNotExecutedAsync(
        AcknowledgePromptResultRequest ack, Func<AcknowledgePromptResultRequest, CancellationToken, Task> ackSubmitPrompt)
    {
        ack.Outcome = PromptResultOutcome.NotExecuted;
        ack.DiagnosticCode = "prompt_preparation_incomplete";
        try { await ackSubmitPrompt(ack, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt acknowledgement {ack.HandoffId} could not be queued ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Runs one prompt handoff at most once. The handoff is checked against the session's
    /// current runtime and then granted by the platform; only GRANTED touches the runtime.
    /// Any other grant outcome drops the delivery without native I/O or an acknowledgement.
    /// After GRANTED every path ends in exactly one acknowledgement: the native response,
    /// NOT_EXECUTED when nothing reached the runtime, or UNKNOWN once native I/O started.
    /// </summary>
    internal static async Task HandleSubmitEditorActionAsync(
        Func<AcknowledgePromptResultRequest, CancellationToken, Task> ackSubmitPrompt,
        Func<ReportSessionEventsRequest, CancellationToken, Task> reportSessionEvents,
        SubmitPromptCommand prompt,
        string runnerId,
        string instanceId,
        Func<SubmitPromptCommand, bool> isPromptRuntimeCurrent,
        Func<GrantPromptExecutionRequest, CancellationToken, Task<GrantPromptExecutionResponse>> grantExecution,
        Func<string, string, string, CancellationToken, Task> ensureBridge,
        Action<string?> setActiveDownloadSession,
        Func<string, string, string?, CancellationToken, Task> activateSessionUploads,
        Func<string, string, string?, IEnumerable<EditorArtifactMetadata>, CancellationToken, Task> seedSessionArtifacts,
        Func<string, Func<Task>> captureSessionArtifactRefresh,
        Action<string, IEnumerable<ModelDownloadRequest>> registerDownloads,
        Func<IEnumerable<ModelDownloadRequest>, CancellationToken, bool, Task<Task>> preparePromptDownloads,
        Func<SubmitPromptCommand, Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>?, Action, CancellationToken, Task<SessionProcessManager.PromptSubmissionResult>> submitPrompt,
        Action<string, string, string, string> mapPrompt,
        CancellationToken cancellationToken,
        Func<SessionProcessManager.SessionPaths?, IEnumerable<ModelDownloadRequest>, bool, CancellationToken, Task>? localModelProjectionReconciler = null,
        Func<string, SessionProcessManager.SessionPaths?>? sessionPathsProvider = null,
        CancellationToken promptDemandCancellation = default,
        Func<string, CancellationToken, Task<bool?>>? isPromptQueued = null,
        Action<Task>? holdPromptDemand = null,
        Action? onGranted = null,
        CancellationToken hostShutdown = default,
        TimeSpan? grantSettleTimeout = null,
        Action<Func<Task>>? settleInBackground = null)
    {
        // Reject malformed wire identities before granting or touching a runtime.
        if (!PromptHandoff.HasValidIdentity(prompt)
            || !Guid.TryParse(runnerId, out var runner) || runner == Guid.Empty || string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("The prompt handoff must name its exact session and runtime.", nameof(prompt));
        // A handoff for a stopped, replaced or unknown runtime is never granted here.
        if (!isPromptRuntimeCurrent(prompt))
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt handoff {prompt.HandoffId} targets a runtime that is not current; dropping it.");
            return;
        }

        var grant = new GrantPromptExecutionRequest
        {
            RunnerId = runnerId,
            SessionId = prompt.SessionId,
            HandoffId = prompt.HandoffId,
            InstanceId = instanceId,
            Runtime = prompt.Runtime?.Clone(),
        };
        // Nothing has been asked yet, so an interrupt or Stop here leaves nothing behind.
        if (cancellationToken.IsCancellationRequested) return;
        var ack = new AcknowledgePromptResultRequest
        {
            RunnerId = runnerId,
            SessionId = prompt.SessionId,
            HandoffId = prompt.HandoffId,
            InstanceId = instanceId,
        };
        // Once a grant request is sent, the platform may record GRANTED for this instance
        // whatever happens here, so an interrupt or Stop never abandons it. This work still
        // ends at once, so neither Stop nor the command loop waits for the grant, and the
        // handoff is remembered as granted, so no redelivery runs it here. The request goes
        // on settling apart from this work, under the host lifetime and within a bound; a
        // GRANTED answer is acknowledged NOT_EXECUTED, which releases the platform's lane.
        var grantLifetime = CancellationTokenSource.CreateLinkedTokenSource(hostShutdown);
        var grantRequest = RequestGrantAsync(grant, grantExecution, prompt.HandoffId, grantLifetime.Token);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => interrupted.TrySetResult()))
            await Task.WhenAny(grantRequest, interrupted.Task).ConfigureAwait(false);
        if (!grantRequest.IsCompleted)
        {
            onGranted?.Invoke();
            grantLifetime.CancelAfter(grantSettleTimeout ?? GrantSettleTimeout);
            (settleInBackground ?? (settle => _ = settle()))(
                () => SettleInterruptedGrantAsync(grantRequest, grantLifetime, ack, ackSubmitPrompt, hostShutdown));
            return;
        }
        grantLifetime.Dispose();
        var permission = await grantRequest.ConfigureAwait(false);
        if (permission is null) return;
        if (permission.Outcome != PromptGrantOutcome.Granted)
        {
            // Another instance holds it, it expired, its runtime moved on or the account
            // cannot start work. None of these is run here or acknowledged by this runner.
            RunnerLog.Info(nameof(ServiceClientManager), $"Prompt handoff {prompt.HandoffId} was not granted ({permission.Outcome}); dropping the delivery.");
            return;
        }
        onGranted?.Invoke();
        if (cancellationToken.IsCancellationRequested)
        {
            // Interrupted or stopped as the grant arrived. Nothing is prepared: by now
            // another session may be this runner's active one, and preparing would claim
            // its downloads and models for this one.
            await AcknowledgeNotExecutedAsync(ack, ackSubmitPrompt).ConfigureAwait(false);
            return;
        }

        var clientId = string.IsNullOrWhiteSpace(prompt.ClientId) ? null : prompt.ClientId.Trim();
        var nativeInvocationStarted = false;
        var streamIncomplete = false;
        // Prompt model demand outlives preparation; this work owns its lifetime.
        // Until the native request starts, cancelling this work (interrupt, Stop)
        // withdraws the demand. From then on the native outcome decides: an
        // accepted prompt may sit in the runtime's queue, so only
        // promptDemandCancellation (Stop, replacement, its terminal event,
        // shutdown) or leaving the queue withdraws it, and a failed or uncertain
        // invocation withdraws it when it settles. The hand-over is atomic with
        // the cancellation callback, so a racing interrupt cannot drop the demand
        // of a prompt the runtime accepted.
        // Owned by the settling demand, which can outlive this command.
        var promptDemandLifetime = new OwnedCancellation(promptDemandCancellation);
        var demandGate = new object();
        var demandFollowsWork = true;
        using var withdrawDemandBeforeInvocation = cancellationToken.Register(() =>
        {
            lock (demandGate)
            {
                if (demandFollowsWork)
                {
                    _ = promptDemandLifetime.RequestAsync();
                }
            }
        });
        Task? promptDemand = null;
        var keepPromptDemand = false;
        string? acceptedPromptId = null;

        if (clientId is not null)
        {
            try
            {
                await ensureBridge(prompt.SessionId, prompt.EditorSid, clientId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt websocket preparation unavailable ({ex.GetType().Name}).");
            }
        }

        try
        {
            // Capture the uploader under this handoff's grant, inside the
            // acknowledgement path. A missing uploader is proven pre-invocation
            // failure; a delayed sweep can never bind a replacement session.
            var capturedRefresh = captureSessionArtifactRefresh(prompt.SessionId);
            Task RefreshCapturedArtifacts(string _) => capturedRefresh();
            if (ShouldPrepareGenerationDependencies(prompt))
            {
                setActiveDownloadSession(prompt.SessionId);
                var promptSessionPaths = sessionPathsProvider?.Invoke(prompt.SessionId);
                var downloads = MaterializeModelDownloadPathsForSession(
                    prompt.Downloads,
                    prompt.EditorRuntimeKind,
                    promptSessionPaths);
                if (localModelProjectionReconciler is not null)
                {
                    await localModelProjectionReconciler(promptSessionPaths, downloads, false, cancellationToken).ConfigureAwait(false);
                }

                var remoteDownloads = FilterRemoteDownloadRequests(downloads);
                if (!string.IsNullOrWhiteSpace(prompt.EditorSid))
                {
                    await activateSessionUploads(prompt.SessionId, prompt.EditorSid, prompt.Owner, cancellationToken).ConfigureAwait(false);
                    var promptArtifacts = BuildPromptArtifactMetadata(prompt);
                    if (promptArtifacts.Count > 0)
                    {
                        await seedSessionArtifacts(prompt.SessionId, prompt.EditorSid, prompt.Owner, promptArtifacts, cancellationToken).ConfigureAwait(false);
                    }
                }
                registerDownloads(prompt.SessionId, remoteDownloads);
                if (ShouldRefreshArtifactsBeforePrompt(prompt))
                {
                    await TryRefreshSessionArtifactsBeforePromptAsync(prompt.SessionId, RefreshCapturedArtifacts, cancellationToken).ConfigureAwait(false);
                }

                promptDemand = await preparePromptDownloads(remoteDownloads, promptDemandLifetime.Token, false).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await submitPrompt(
                prompt,
                ShouldStreamHttpResponse(prompt)
                    ? async (eventType, chunk, token) =>
                    {
                        try
                        {
                            await ReportHttpResponseEventAsync(reportSessionEvents, runnerId, prompt, eventType, chunk, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // Live fan-out loss cannot cancel an accepted consuming reader;
                            // the acknowledgement still reports how the stream ended, and
                            // that the page did not get all of it.
                            streamIncomplete = true;
                            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt live relay unavailable ({ex.GetType().Name}).");
                        }
                    }
            : null,
                () =>
                {
                    lock (demandGate)
                    {
                        demandFollowsWork = false;
                    }
                    nativeInvocationStarted = true;
                },
                cancellationToken).ConfigureAwait(false);
            keepPromptDemand = result.Success;
            if (result.Success && !result.BodyOmitted)
            {
                // ComfyUI names the prompt it queued; the platform injects no id of its own.
                acceptedPromptId = RegistrationPolicy.TryExtractPromptId(System.Text.Encoding.UTF8.GetString(result.Body));
            }
            ack.Outcome = PromptResultOutcome.NativeResponse;
            ack.StatusCode = result.StatusCode;
            ack.Body = ByteString.CopyFrom(result.Body);
            ack.ContentType = result.ContentType;
            if (result.BodyOmitted)
            {
                ack.BodyOmitted = true;
                ack.DiagnosticCode = PromptProtocolCodes.NativeResponseBodyOmitted;
            }
            else if (streamIncomplete)
            {
                ack.DiagnosticCode = PromptProtocolCodes.StreamIncomplete;
            }

            if (clientId is not null && !string.IsNullOrWhiteSpace(acceptedPromptId))
            {
                mapPrompt(prompt.SessionId, clientId, acceptedPromptId!, prompt.HandoffId);
            }

            if (result.Success && ShouldRefreshArtifactsAfterPrompt(prompt))
            {
                KickOffPromptArtifactRefresh(prompt.SessionId, prompt.HandoffId, RefreshCapturedArtifacts, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), nativeInvocationStarted
                ? $"Prompt handoff {prompt.HandoffId} ended with {ex.GetType().Name}; its native result is unknown."
                : $"Prompt handoff {prompt.HandoffId} ended with {ex.GetType().Name} before native request I/O.");
            // A native response already recorded stays the result.
            if (ack.Outcome == PromptResultOutcome.Unspecified)
            {
                ack.Outcome = nativeInvocationStarted ? PromptResultOutcome.Unknown : PromptResultOutcome.NotExecuted;
                ack.DiagnosticCode = nativeInvocationStarted
                    ? "prompt_native_result_unknown"
                    : ex is ModelDownloadsDisabledException
                        ? ModelDownloadsDisabledException.DiagnosticCode
                        : "prompt_preparation_incomplete";
            }
        }

        // ackSubmitPrompt must retain this exact acknowledgement and retry it until it is
        // delivered or the host shuts down, independently of the prompt's own cancellation:
        // the handoff is never run again, so this is its only result. The command loop wires
        // it to AcknowledgeRuntimeCommandWithRetryAsync on the host shutdown token, so this
        // only sees a failure to hand it over.
        try
        {
            var ackCancellationToken = cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            await ackSubmitPrompt(ack, ackCancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt acknowledgement {prompt.HandoffId} could not be queued ({ex.GetType().Name}).");
        }

        withdrawDemandBeforeInvocation.Dispose();
        var settling = SettlePromptDemandAsync(
            promptDemand,
            keepPromptDemand,
            promptDemandLifetime,
            isPromptQueued is not null && !string.IsNullOrWhiteSpace(acceptedPromptId)
                ? BindPromptQueueProbe(isPromptQueued, acceptedPromptId!)
                : null);
        if (holdPromptDemand is not null && !settling.IsCompleted)
        {
            // The command ends with its acknowledgement; only the demand stays, so the
            // completed command and its body are released while hydration settles.
            holdPromptDemand(settling);
            return;
        }

        await settling.ConfigureAwait(false);
    }

    // These factories isolate the settling callbacks from the full command closure.
    private static Func<string, CancellationToken, Task<bool?>> CreatePromptQueueProbe(
        Func<string, RuntimeIdentity, CancellationToken, Task<ComfyPromptQueue?>> readQueue,
        string sessionId, RuntimeIdentity runtime)
        => async (promptId, token) => (await readQueue(sessionId, runtime, token).ConfigureAwait(false))?.Contains(promptId);

    private static Func<CancellationToken, Task<bool?>> BindPromptQueueProbe(
        Func<string, CancellationToken, Task<bool?>> probe, string promptId)
        => token => probe(promptId, token);

    /// <summary>
    /// A queued prompt keeps its ordered model demand until hydration settles, after
    /// its acknowledgement was handed over, or until Stop or its terminal event
    /// withdraws it. A rejected or failed prompt withdraws it at once.
    /// </summary>
    internal static TimeSpan PromptDemandQueueProbeInterval { get; set; } = TimeSpan.FromSeconds(10);

    internal static async Task SettlePromptDemandAsync(
        Task? demand,
        bool keep,
        OwnedCancellation lifetime,
        Func<CancellationToken, Task<bool?>>? isStillQueued = null)
    {
        try
        {
            if (demand is null || !keep)
            {
                // Withdrawal requests cancellation without running user callbacks
                // here. Their failure cannot bypass the original demand join.
                _ = lifetime.RequestAsync();
            }
            else if (isStillQueued is not null)
            {
                await WithdrawOnceDequeuedAsync(demand, lifetime, isStillQueued).ConfigureAwait(false);
            }

            if (demand is not null)
            {
                try
                {
                    await demand.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt model hydration ended with {ex.GetType().Name}; managed reads remain gated.");
                }
            }
        }
        finally
        {
            // Even preparation that returned no demand can have registered
            // callbacks. This settling owner joins their first cancellation task.
            _ = lifetime.RequestAsync();
            await lifetime.CompleteAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ComfyUI sends no terminal event for a prompt removed from its queue (queue
    /// delete or clear, a pending job cancel), and a terminal frame can be missed
    /// or arrive before the prompt id is mapped. The queue is the fallback: once
    /// the accepted prompt is no longer in it, nothing will read its models. An
    /// unreadable queue keeps the demand.
    /// </summary>
    private static async Task WithdrawOnceDequeuedAsync(
        Task demand,
        OwnedCancellation lifetime,
        Func<CancellationToken, Task<bool?>> isStillQueued)
    {
        while (!demand.IsCompleted && !lifetime.Token.IsCancellationRequested)
        {
            bool? queued;
            try
            {
                queued = await isStillQueued(lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (lifetime.Token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                queued = null;
            }

            if (queued == false)
            {
                _ = lifetime.RequestAsync();
                return;
            }

            await Task.WhenAny(demand, Task.Delay(PromptDemandQueueProbeInterval, lifetime.Token)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// ComfyUI interrupts and cancels cooperatively, which a loader blocked on a
    /// model download never sees. Once ComfyUI accepted a global interrupt while a
    /// prompt runs, or a targeted interrupt or job cancel naming the running
    /// prompt, the session's pending model reads fail so that prompt ends now.
    /// This runs only after the control succeeded: failing reads first could end
    /// the prompt before the interrupt arrives, letting it hit the next queued one.
    /// </summary>
    internal static async Task InterruptBlockedReadsForCancelAsync(
        RelayEditorRuntimeRequestCommand request,
        AcknowledgeEditorRuntimeRequestRequest response,
        Func<string, RuntimeIdentity, CancellationToken, Task<ComfyPromptQueue?>> readQueue,
        Action<string> interruptSessionReads,
        CancellationToken cancellationToken)
    {
        if (request.Kind != EditorRuntimeRequestKind.Control || !response.TransportSucceeded ||
            response.StatusCode is < 200 or >= 300)
        {
            return;
        }

        var global = ComfyRuntimeRoutes.IsGlobalInterrupt(request.Path, request.Body.Span);
        IReadOnlyList<string> targets = global ? [] : ComfyCancelTargets.PromptIds(request.Path, request.Body.Span);
        if (!global && targets.Count == 0)
        {
            return;
        }

        var queue = await readQueue(
            request.SessionId,
            RuntimeIdentity.From(request.Runtime),
            cancellationToken).ConfigureAwait(false);
        if (queue is not null && (global ? queue.Running.Count > 0 : targets.Any(queue.Running.Contains)))
        {
            interruptSessionReads(request.SessionId);
        }
    }

    /// <summary>
    /// A terminal event ends the demand of the handoff its prompt id was mapped to, on
    /// the same runtime. An unmapped prompt's demand ends once it leaves the queue.
    /// </summary>
    internal static bool IsFinishedPrompt(PromptWorkIdentity prompt, ComfySessionEventRelay.PromptTerminalEvent finished)
        => PromptTargetsRuntime(prompt, finished.SessionId, finished.Runtime)
           && finished.HandoffId is not null
           && string.Equals(prompt.HandoffId, finished.HandoffId, StringComparison.Ordinal);

    internal static bool TargetsPromptRuntime(RelayEditorRuntimeRequestCommand request, PromptWorkIdentity prompt)
        => request.Runtime is { RuntimeOrder: > 0 } runtime
            && Guid.TryParse(request.SessionId, out var session) && session != Guid.Empty
            && Guid.TryParse(prompt.SessionId, out var promptSession) && session == promptSession
            && Guid.TryParse(runtime.RuntimeId, out var runtimeId) && runtimeId != Guid.Empty
            && Guid.TryParse(prompt.Runtime.RuntimeId, out var promptRuntimeId) && runtimeId == promptRuntimeId;

    private static bool ShouldStreamHttpResponse(SubmitPromptCommand prompt)
        => string.Equals(prompt.EditorRuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
           && string.Equals(prompt.Request?.Method, "GET", StringComparison.OrdinalIgnoreCase)
           && PathMatchesRoute((prompt.Request?.Path ?? string.Empty).Trim().TrimStart('/'), "queue/data");

    internal static bool IsSideEffectFreePromptRelay(SubmitPromptCommand prompt)
        => string.Equals(prompt.EditorRuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
           && string.Equals(prompt.Request?.Method, "POST", StringComparison.OrdinalIgnoreCase)
           && PathMatchesRoute((prompt.Request?.Path ?? string.Empty).Trim().TrimStart('/'), "internal/progress");

    private static bool ShouldRefreshArtifactsBeforePrompt(SubmitPromptCommand prompt)
        => ShouldPrepareGenerationDependencies(prompt)
           && string.Equals(prompt.EditorRuntimeKind, "comfyui", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(prompt.EditorSid);

    internal static bool ShouldPrepareGenerationDependencies(SubmitPromptCommand prompt)
        => !string.Equals(prompt.EditorRuntimeKind, "comfyui", StringComparison.OrdinalIgnoreCase) ||
           ComfyRuntimeRoutes.Classify(prompt.Request?.Method, prompt.Request?.Path) == ComfyRuntimeRouteOwnership.DurableGeneration;

    private static bool ShouldRefreshArtifactsAfterPrompt(SubmitPromptCommand prompt)
        => ShouldPrepareGenerationDependencies(prompt) && !IsSideEffectFreePromptRelay(prompt);

    private static async Task TryRefreshSessionArtifactsBeforePromptAsync(
        string sessionId,
        Func<string, Task> refreshSessionArtifacts,
        CancellationToken cancellationToken)
    {
        try
        {
            await refreshSessionArtifacts(sessionId).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to refresh artifacts before prompt for session {sessionId}: {ex.Message}");
        }
    }

    internal static IReadOnlyList<ModelDownloadRequest> FilterRemoteDownloadRequests(IEnumerable<ModelDownloadRequest>? downloads)
        => downloads?
            .Where(static download => !LocalModelProjector.IsLocalProjectionRequest(download))
            .ToArray()
           ?? [];

    internal static IReadOnlyList<ModelDownloadRequest> MaterializeModelDownloadPathsForSession(
        IEnumerable<ModelDownloadRequest>? downloads,
        string? runtimeKind,
        SessionProcessManager.SessionPaths? sessionPaths)
    {
        if (downloads is null)
        {
            return [];
        }

        var snapshot = downloads
            .Where(static download => download is not null)
            .ToArray();
        if (!string.Equals(runtimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase))
        {
            return snapshot;
        }

        if (snapshot.Length == 0)
        {
            return snapshot;
        }

        if (sessionPaths is not { ModelsDirectory: { Length: > 0 } modelsDirectory })
        {
            throw new InvalidOperationException("Forge model downloads require active session paths.");
        }

        // Models live in the runner's cache, which outlasts the session; the session's own models
        // directory links to them (SessionModelLinks). Without a cache they go into the session.
        var cacheDirectory = sessionPaths.Value.ModelCacheDirectory is { Length: > 0 } cache ? cache : null;
        var rewritten = new ModelDownloadRequest[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            var download = snapshot[i];
            var clone = download.Clone();
            clone.DestinationPath = BuildModelPath(
                cacheDirectory ?? modelsDirectory,
                download.RelativeModelPath,
                download.Filename,
                sessionOwned: cacheDirectory is null);
            rewritten[i] = clone;
        }

        return rewritten;
    }

    /// <summary>
    /// Where <paramref name="relativeModelPath"/> goes beneath <paramref name="modelsDirectory"/>, refusing any
    /// path that leaves it. In a <paramref name="sessionOwned"/> directory, symlinked components are refused too.
    /// The cache is the agent's own: the download service confines what is downloaded there, and a local
    /// model's entry in it is a link.
    /// </summary>
    internal static string BuildModelPath(string modelsDirectory, string? relativeModelPath, string? filename, bool sessionOwned)
    {
        if (string.IsNullOrWhiteSpace(relativeModelPath))
        {
            throw new InvalidOperationException($"Forge model download '{filename}' is missing a relative model path.");
        }

        var normalizedRelativePath = relativeModelPath
            .Trim()
            .Replace('\\', Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(normalizedRelativePath) ||
            Path.IsPathFullyQualified(normalizedRelativePath))
        {
            throw new InvalidOperationException($"Forge model download '{filename}' has an invalid relative model path.");
        }

        var modelsRoot = NormalizeFullPath(modelsDirectory);
        var destinationPath = NormalizeFullPath(Path.Combine(modelsRoot, normalizedRelativePath));
        if (!IsPathWithinRoot(destinationPath, modelsRoot))
        {
            throw new InvalidOperationException($"Forge model download '{filename}' escapes the session model root.");
        }

        if (sessionOwned)
        {
            // The session user owns its models directory; refuse symlinked components so
            // root-run downloads and placeholders cannot be redirected out of it.
            ModelDestinationPolicy.ConfineToSessionRoot(modelsRoot, destinationPath);
        }

        return destinationPath;
    }

    private static string NormalizeFullPath(string path)
        => Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsPathWithinRoot(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return string.Equals(relative, ".", StringComparison.Ordinal) ||
            (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative));
    }

    private static IReadOnlyList<EditorArtifactMetadata> BuildPromptArtifactMetadata(SubmitPromptCommand prompt)
    {
        if (prompt.ArtifactReferences.Count == 0 || string.IsNullOrWhiteSpace(prompt.EditorSid))
        {
            return [];
        }

        var results = new List<EditorArtifactMetadata>(prompt.ArtifactReferences.Count);
        foreach (var reference in prompt.ArtifactReferences)
        {
            if (string.IsNullOrWhiteSpace(reference.Filename))
            {
                continue;
            }

            results.Add(new EditorArtifactMetadata
            {
                EditorSid = prompt.EditorSid,
                Filename = reference.Filename,
                Type = string.IsNullOrWhiteSpace(reference.Type) ? "temp" : reference.Type,
                Subfolder = reference.Subfolder ?? string.Empty
            });
        }

        return results;
    }

    private static bool PathMatchesRoute(string path, string route)
        => path.Equals(route, StringComparison.OrdinalIgnoreCase)
           || path.StartsWith($"{route}/", StringComparison.OrdinalIgnoreCase);

    private static async Task ReportHttpResponseEventAsync(
        Func<ReportSessionEventsRequest, CancellationToken, Task> reportSessionEvents,
        string runnerId,
        SubmitPromptCommand prompt,
        string eventType,
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        var request = new ReportSessionEventsRequest();
        request.Events.Add(new SessionStreamEvent
        {
            RunnerId = runnerId,
            SessionId = prompt.SessionId,
            EditorSid = prompt.EditorSid,
            ClientId = $"http-relay:{prompt.HandoffId}",
            HandoffId = prompt.HandoffId,
            EventType = string.IsNullOrWhiteSpace(eventType) ? HttpResponseChunkEventType : eventType,
            FrameType = SessionStreamFrameType.Binary,
            Payload = ByteString.CopyFrom(chunk.ToArray()),
            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            Runtime = prompt.Runtime?.Clone(),
        });

        await reportSessionEvents(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunPromptArtifactRefreshSweepAsync(
        string sessionId,
        Func<string, Task> refreshSessionArtifacts,
        IReadOnlyList<TimeSpan> delays,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var delay in delays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }

                await refreshSessionArtifacts(sessionId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to refresh artifacts for session {sessionId}: {ex.Message}");
        }
    }

    private static void KickOffPromptArtifactRefresh(
        string sessionId,
        string handoffId,
        Func<string, Task> refreshSessionArtifacts,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await refreshSessionArtifacts(sessionId).ConfigureAwait(false);
                    await RunPromptArtifactRefreshSweepAsync(sessionId, refreshSessionArtifacts, PromptArtifactRefreshDelays.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
                catch (Exception ex)
                {
                    RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to refresh artifacts after prompt handoff {handoffId}: {ex.Message}");
                }
            },
            CancellationToken.None);
    }

    private static string BuildLocalModelSummary(LocalModelSnapshot snapshot)
    {
        if (snapshot.Models.Count == 0)
        {
            return "Local model scan summary: 0 models detected.";
        }

        var folderParts = snapshot.FolderStats
            .Select(stat => $"{stat.Folder}:{stat.ModelCount} ({FormatBytes(stat.TotalBytes)})");
        return $"Local model scan summary: {snapshot.Models.Count} models | {string.Join(", ", folderParts)}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        var kb = bytes / 1024d;
        if (kb < 1024) return $"{kb:F1} KB";
        var mb = kb / 1024d;
        if (mb < 1024) return $"{mb:F1} MB";
        var gb = mb / 1024d;
        return $"{gb:F2} GB";
    }

    /// <summary>The agent's heartbeat: the shared request, with this machine's memory read afresh each time.</summary>
    private static RegisterRunnerRequest BuildStatusUpdateRequest(
        string runnerId,
        string instanceId,
        string version,
        RunnerResources resources,
        Func<RuntimeStatus> describeRuntime,
        IReadOnlyList<string> supportedEditorTemplates,
        Func<RunnerSealedStorage>? describeSealedStorage,
        string launchId)
    {
        resources.SystemRamBytes = ResourceDetection.GetTotalRamBytes();
        return RunnerRegistrationLoop.BuildExecutorRequest(
            runnerId,
            instanceId,
            version,
            resources,
            describeRuntime,
            supportedEditorTemplates,
            RunnerLog.CreateLogger(nameof(ServiceClientManager)),
            describeSealedStorage,
            launchId);
    }

    private static SyncLocalModelsRequest BuildLocalModelsRequest(string runnerId, LocalModelSnapshot snapshot, bool fullSnapshot)
    {
        var request = new SyncLocalModelsRequest
        {
            RunnerId = runnerId,
            SnapshotDigest = snapshot.SelectionDigest,
            FullSnapshot = fullSnapshot
        };
        if (fullSnapshot)
        {
            request.SelectionHashes.AddRange(snapshot.SelectionHashes);
        }
        return request;
    }
}

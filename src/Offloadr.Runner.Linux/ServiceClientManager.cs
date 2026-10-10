using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Offloadr.Runner.V1;
using Offloadr.Common.V1;
using Offloadr.EditorRuntime.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;

namespace Offloadr.Runner.Linux;

internal static class ServiceClientManager
{
    internal const string HttpResponseChunkEventType = "http.response.chunk";

    internal sealed class TransientAcknowledgementCache(
        int maxEntries = 256,
        long maxBytes = 64L * 1024 * 1024)
    {
        private sealed record CacheEntry(
            AcknowledgeEditorRuntimeRequestRequest Response,
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

        public void Set(AcknowledgeEditorRuntimeRequestRequest response)
        {
            var cached = response;
            var sizeBytes = cached.CalculateSize();
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
                _entries[cached.RequestId] = new CacheEntry(cached, sizeBytes, insertionNode);
                _retainedBytes += sizeBytes;
            }
        }

        public bool TryGet(string requestId, out AcknowledgeEditorRuntimeRequestRequest response)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(requestId, out var cached))
                {
                    response = cached.Response.Clone();
                    return true;
                }
            }

            response = null!;
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

        /// <summary>The request already ran here, but its result is no longer retained.</summary>
        AlreadyExecuted,

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
        private readonly Dictionary<string, List<string>> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _executedUntil = new(StringComparer.Ordinal);
        private readonly PriorityQueue<string, DateTime> _executedByDeadline = new();
        private readonly Dictionary<string, DateTime> _deadlines = new(StringComparer.Ordinal);

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
            out AcknowledgeEditorRuntimeRequestRequest? replay,
            DateTime? nowUtc = null)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            lock (_gate)
            {
                if (completed.TryGet(request.RequestId, out var cached))
                {
                    cached.DeliveryId = request.DeliveryId;
                    replay = cached;
                    return TransientRequestAdmission.Replay;
                }

                replay = null;
                if (_inFlight.TryGetValue(request.RequestId, out var deliveries))
                {
                    if (!deliveries.Contains(request.DeliveryId, StringComparer.Ordinal))
                    {
                        deliveries.Add(request.DeliveryId);
                    }

                    return TransientRequestAdmission.Joined;
                }

                PruneExecutedMarkers(now);
                if (_executedUntil.ContainsKey(request.RequestId))
                {
                    // Never run a request twice, even when its result was evicted.
                    return TransientRequestAdmission.AlreadyExecuted;
                }

                if (IsExpired(request, now))
                {
                    return TransientRequestAdmission.Expired;
                }

                if (_executedUntil.Count + _inFlight.Count >= MaxExecutedMarkers)
                {
                    return TransientRequestAdmission.Saturated;
                }

                _inFlight.Add(request.RequestId, [request.DeliveryId]);
                _deadlines[request.RequestId] = request.ExpiresUtc.ToDateTime();
                return TransientRequestAdmission.Execute;
            }
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
                completed.Set(result);
                if (_deadlines.Remove(result.RequestId, out var deadline))
                {
                    RememberExecuted(result.RequestId, deadline);
                }
                if (!_inFlight.Remove(result.RequestId, out var deliveries))
                {
                    deliveries = [result.DeliveryId];
                }

                return deliveries
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

    internal sealed class RuntimeCommandDeduplicationCache(int maxEntries = 16_384)
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _commandIds = new(StringComparer.Ordinal);
        private readonly Queue<string> _insertionOrder = new();
        private readonly int _maxEntries = maxEntries > 0 ? maxEntries : throw new ArgumentOutOfRangeException(nameof(maxEntries));

        public int Count
        {
            get { lock (_gate) return _commandIds.Count; }
        }

        public bool TryAdd(string commandId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
            lock (_gate)
            {
                if (!_commandIds.Add(commandId))
                {
                    return false;
                }

                _insertionOrder.Enqueue(commandId);
                while (_commandIds.Count > _maxEntries)
                {
                    _commandIds.Remove(_insertionOrder.Dequeue());
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Cancellation for in-flight transient editor requests, per exact runtime.
    /// A request leases its runtime's source for its lifetime; the source is
    /// removed and disposed with the last lease, so entries never outlive their
    /// requests and a cancelled source is never read after disposal.
    /// </summary>
    internal sealed class TransientCancellationRegistry(CancellationToken shutdown)
    {
        internal readonly record struct Key(string SessionId, RuntimeIdentity Runtime);

        internal sealed class Entry(CancellationTokenSource source)
        {
            public CancellationTokenSource Source { get; } = source;
            public int Leases { get; set; }
        }

        internal sealed class Lease : IDisposable
        {
            private readonly TransientCancellationRegistry _owner;
            private readonly Key _key;
            private readonly Entry _entry;
            private int _disposed;

            public Lease(TransientCancellationRegistry owner, Key key, Entry entry)
            {
                _owner = owner;
                _key = key;
                _entry = entry;
                Token = entry.Source.Token;
            }

            public CancellationToken Token { get; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _owner.Release(_key, _entry);
                }
            }
        }

        private readonly object _gate = new();
        private readonly Dictionary<Key, Entry> _entries = new();

        public int Count
        {
            get { lock (_gate) return _entries.Count; }
        }

        public Lease Acquire(string? sessionId, ulong lifecycleGeneration, ulong runtimeEpoch, string? runtimeInstanceId)
        {
            var key = new Key(
                sessionId?.Trim() ?? string.Empty,
                new RuntimeIdentity(lifecycleGeneration, runtimeEpoch, runtimeInstanceId ?? string.Empty).Normalized());
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry(CancellationTokenSource.CreateLinkedTokenSource(shutdown));
                    _entries.Add(key, entry);
                }

                entry.Leases++;
                return new Lease(this, key, entry);
            }
        }

        public void CancelSession(string? sessionId)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            Cancel(key => string.Equals(key.SessionId, normalizedSessionId, StringComparison.Ordinal));
        }

        public void CancelRuntime(string? sessionId, RuntimeIdentity runtime)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            var normalizedRuntime = runtime.Normalized();
            Cancel(key => string.Equals(key.SessionId, normalizedSessionId, StringComparison.Ordinal) &&
                          key.Runtime == normalizedRuntime);
        }

        public void CancelAll() => Cancel(_ => true);

        private void Cancel(Func<Key, bool> matches)
        {
            List<Entry> cancelled;
            lock (_gate)
            {
                cancelled = [];
                foreach (var key in _entries.Keys.Where(matches).ToArray())
                {
                    cancelled.Add(_entries[key]);
                    _entries.Remove(key);
                }
            }

            // Later requests for the same runtime lease a fresh source.
            foreach (var entry in cancelled)
            {
                try
                {
                    entry.Source.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Its last request already completed and released it.
                }
            }
        }

        private void Release(Key key, Entry entry)
        {
            lock (_gate)
            {
                if (--entry.Leases > 0)
                {
                    return;
                }

                if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(key);
                }
            }

            entry.Source.Dispose();
        }
    }

    internal sealed class TrackedCommandWork
    {
        public Task? Task { get; set; }
        public required CancellationTokenSource CancellationSource { get; init; }
        public required string SessionId { get; init; }
        public RuntimeIdentity RuntimeIdentity { get; init; }

        public void Cancel()
        {
            try
            {
                CancellationSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The work already finished and released its source.
            }
        }
    }

    /// <summary>
    /// The physical operations and state one runtime command handler needs. The
    /// command stream binds them to the live services; tests bind fakes.
    /// </summary>
    internal sealed class RuntimeCommandDependencies
    {
        public required string RunnerId { get; init; }
        public required IRunnerSessionSink Sink { get; init; }
        public required RunnerCommandWorkState WorkState { get; init; }
        public required LogicalSessionState LogicalSessionState { get; init; }
        public required RuntimeIdentityRegistry RuntimeIdentities { get; init; }
        public required TransientCancellationRegistry TransientCancellation { get; init; }
        public required Func<string> GetActiveRuntimeSessionId { get; init; }
        public required Func<StartSessionCommand, CancellationToken, Task> StartRuntimeAndWaitForReady { get; init; }
        public required Func<string, Func<string, CancellationToken, Task>?, CancellationToken, Task> StopRuntime { get; init; }
        public required Func<string, RuntimeIdentity, Func<string, CancellationToken, Task>?, CancellationToken, Task<bool>> StopRuntimeIfIdentityMatches { get; init; }
        public required Func<string, RuntimeIdentity?> GetTrackedRuntime { get; init; }
        public required Func<string, Task> StopSessionRelay { get; init; }
        public required Func<string, Task> StopArtifactUploads { get; init; }
        public required Func<string, Task> StopWorkspaceMirrors { get; init; }
        public required Action<string?> CancelSessionDownloads { get; init; }
        public required Func<string?> GetDownloadActiveSessionId { get; init; }
        public required Action<string?> SetDownloadActiveSession { get; init; }
        public required Func<Action, CancellationToken, Task> ActivateSession { get; init; }
        public required CancellationToken Shutdown { get; init; }
    }

    internal sealed class RunnerCommandWorkState(CancellationToken shutdown)
    {
        private readonly object _gate = new();
        public PromptCommandWorkSet Prompts { get; } = new(shutdown);
        private TrackedCommandWork? _activeStartup;

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
                }
            }
        }
    }

    internal enum RuntimeOrder
    {
        Older,
        Same,
        Newer,
    }

    internal sealed class LogicalSessionState(string initialSessionId)
    {
        private sealed record State(string SessionId, RuntimeIdentity? RuntimeIdentity, uint RestartRevision = 0);

        private State _state = new(initialSessionId?.Trim() ?? string.Empty, null);

        public string GetActiveSessionId() => Volatile.Read(ref _state).SessionId;

        public bool TryGetRuntime(string? sessionId, out RuntimeIdentity runtimeIdentity, out uint restartRevision)
        {
            var current = Volatile.Read(ref _state);
            if (current.RuntimeIdentity is { } identity &&
                string.Equals(current.SessionId, sessionId?.Trim() ?? string.Empty, StringComparison.Ordinal))
            {
                runtimeIdentity = identity;
                restartRevision = current.RestartRevision;
                return true;
            }

            runtimeIdentity = default;
            restartRevision = 0;
            return false;
        }

        /// <summary>
        /// Orders a candidate runtime against an existing one by lifecycle generation,
        /// then runtime epoch, then restart revision. An equal position is the same
        /// runtime only when the instance matches; otherwise it is a conflicting,
        /// therefore older, delivery.
        /// </summary>
        public static RuntimeOrder Compare(
            RuntimeIdentity candidate,
            uint candidateRevision,
            RuntimeIdentity existing,
            uint existingRevision)
        {
            var order = candidate.LifecycleGeneration.CompareTo(existing.LifecycleGeneration);
            if (order == 0) order = candidate.RuntimeEpoch.CompareTo(existing.RuntimeEpoch);
            if (order == 0) order = candidateRevision.CompareTo(existingRevision);
            return order switch
            {
                > 0 => RuntimeOrder.Newer,
                < 0 => RuntimeOrder.Older,
                _ => candidate.SameRuntime(existing) ? RuntimeOrder.Same : RuntimeOrder.Older,
            };
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

        /// <summary>True when the same session already holds a runtime newer than the candidate.</summary>
        public bool IsSuperseded(string? sessionId, RuntimeIdentity candidate, uint restartRevision)
            => TryGetRuntime(sessionId, out var current, out var currentRevision) &&
               Compare(candidate, restartRevision, current, currentRevision) == RuntimeOrder.Older;

        /// <summary>
        /// True when a start is for a session that already ended here at that generation, or
        /// is older than the newest assignment seen. Changes nothing, so it can run before any
        /// tracked work is cancelled.
        /// </summary>
        public bool IsStaleStart(string? sessionId, RuntimeIdentity candidate, ulong assignmentSequence)
            => IsRetired(sessionId?.Trim() ?? string.Empty, candidate) || IsOlderAssignment(assignmentSequence);

        /// <summary>
        /// Records an assignment as soon as its start arrives, so an older start delivered
        /// afterwards is refused even while this one is still starting.
        /// </summary>
        public void ObserveAssignment(ulong assignmentSequence) => RecordAssignment(assignmentSequence);

        /// <summary>
        /// Adopts the runtime only when it is the same as or newer than the current
        /// runtime of the same session. Another active session is replaced only when
        /// <paramref name="replaceOtherSession"/> is set.
        /// </summary>
        public bool TryAdoptRuntime(
            string? sessionId,
            RuntimeIdentity candidate,
            uint restartRevision,
            bool replaceOtherSession,
            ulong assignmentSequence = 0)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            candidate = candidate with { RuntimeInstanceId = candidate.RuntimeInstanceId?.Trim() ?? string.Empty };
            if (normalizedSessionId.Length == 0 || !candidate.IsValid || IsRetired(normalizedSessionId, candidate) ||
                IsOlderAssignment(assignmentSequence))
            {
                return false;
            }

            while (true)
            {
                var current = Volatile.Read(ref _state);
                var next = new State(normalizedSessionId, candidate, restartRevision);
                if (current.SessionId.Length > 0 &&
                    !string.Equals(current.SessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    if (!replaceOtherSession)
                    {
                        return false;
                    }

                    if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, current), current))
                    {
                        // The replaced session has ended here; a late start for it must not return.
                        Retire(current);
                        RecordAssignment(assignmentSequence);
                        return true;
                    }

                    continue;
                }
                else if (current.RuntimeIdentity is { } existing)
                {
                    var order = Compare(candidate, restartRevision, existing, current.RestartRevision);
                    if (order == RuntimeOrder.Older)
                    {
                        return false;
                    }

                    if (order == RuntimeOrder.Same)
                    {
                        RecordAssignment(assignmentSequence);
                        return true;
                    }
                }

                if (ReferenceEquals(Interlocked.CompareExchange(ref _state, next, current), current))
                {
                    RecordAssignment(assignmentSequence);
                    return true;
                }
            }
        }

        public void SetActiveRuntime(
            string? sessionId,
            ulong lifecycleGeneration,
            ulong runtimeEpoch,
            string? runtimeInstanceId)
        {
            var normalizedSessionId = sessionId?.Trim() ?? string.Empty;
            var runtimeIdentity = new RuntimeIdentity(
                lifecycleGeneration,
                runtimeEpoch,
                runtimeInstanceId?.Trim() ?? string.Empty);
            Volatile.Write(
                ref _state,
                new State(normalizedSessionId, runtimeIdentity.IsValid ? runtimeIdentity : null));
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

        public void ClearIfMatches(
            string? sessionId,
            ulong lifecycleGeneration,
            ulong runtimeEpoch,
            string? runtimeInstanceId)
        {
            var expectedSessionId = sessionId?.Trim() ?? string.Empty;
            var expectedRuntimeIdentity = new RuntimeIdentity(
                lifecycleGeneration,
                runtimeEpoch,
                runtimeInstanceId?.Trim() ?? string.Empty);
            while (true)
            {
                var current = Volatile.Read(ref _state);
                if (!string.Equals(current.SessionId, expectedSessionId, StringComparison.Ordinal) ||
                    current.RuntimeIdentity is not { } currentRuntime || !currentRuntime.SameRuntime(expectedRuntimeIdentity))
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
        /// Clears the session after a Stop and remembers it as ended, so a delayed or
        /// redelivered start for the same lifecycle generation cannot bring it back.
        /// </summary>
        public void ClearAndRetire(string? sessionId, ulong stoppedGeneration = 0)
        {
            var expected = sessionId?.Trim() ?? string.Empty;
            if (expected.Length > 0 && stoppedGeneration > 0)
            {
                // The Stop may arrive before its session's start was adopted here.
                Retire(new State(expected, new RuntimeIdentity(stoppedGeneration, 1, "stopped")));
            }

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
                    Retire(current);
                    return;
                }
            }
        }

        // Sessions that ended on this runner, by the highest lifecycle generation they ran
        // at. A start is only fenced against its own session's generations: ordering across
        // different sessions needs an allocation order only the control plane has.
        // Bounded memory; once the control plane sends assignment sequences those fence every
        // start regardless of how many sessions ended here.
        private const int MaxRetiredSessions = 16_384;
        private readonly object _retiredGate = new();
        private readonly Dictionary<string, ulong> _retiredGenerations = new(StringComparer.Ordinal);
        private readonly Queue<string> _retiredOrder = new();

        private void Retire(State ended)
        {
            if (ended.SessionId.Length == 0 || ended.RuntimeIdentity is not { } runtime)
            {
                return;
            }

            lock (_retiredGate)
            {
                if (_retiredGenerations.TryGetValue(ended.SessionId, out var known))
                {
                    _retiredGenerations[ended.SessionId] = Math.Max(known, runtime.LifecycleGeneration);
                    return;
                }

                _retiredGenerations[ended.SessionId] = runtime.LifecycleGeneration;
                _retiredOrder.Enqueue(ended.SessionId);
                while (_retiredOrder.Count > MaxRetiredSessions)
                {
                    _retiredGenerations.Remove(_retiredOrder.Dequeue());
                }
            }
        }

        // Newest session assignment seen on this runner. Zero until the control plane sends
        // assignment sequences; starts without one are not ordered across sessions.
        private ulong _newestAssignment;

        private bool IsOlderAssignment(ulong assignmentSequence)
            => assignmentSequence > 0 && assignmentSequence < Interlocked.Read(ref _newestAssignment);

        private void RecordAssignment(ulong assignmentSequence)
        {
            var newest = Interlocked.Read(ref _newestAssignment);
            while (assignmentSequence > newest)
            {
                var observed = Interlocked.CompareExchange(ref _newestAssignment, assignmentSequence, newest);
                if (observed == newest)
                {
                    return;
                }

                newest = observed;
            }
        }

        private bool IsRetired(string sessionId, RuntimeIdentity candidate)
        {
            lock (_retiredGate)
            {
                return _retiredGenerations.TryGetValue(sessionId, out var retired) &&
                       candidate.LifecycleGeneration <= retired;
            }
        }
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
        string offloadrApiUrl,
        bool traceHttp,
        string runnerSecret,
        string runnerId,
        string instanceId,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        TimeSpan rpcTimeout,
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
        Action<IEnumerable<ModelDownloadRequest>, bool>? localModelProjectionReconciler,
        TimeSpan localModelsScanInterval,
        RuntimeIdentityRegistry runtimeIdentities,
        CancellationToken shutdown)
    {
        using var statusChannel = GrpcChannelManager.CreateChannel(offloadrApiUrl, traceHttp);
        using var commandChannel = GrpcChannelManager.CreateChannel(offloadrApiUrl, traceHttp);
        using var sessionEventChannel = GrpcChannelManager.CreateChannel(offloadrApiUrl, traceHttp);
        using var opsChannel = GrpcChannelManager.CreateChannel(offloadrApiUrl, traceHttp);

        var statusClient = CreateAuthenticatedClient(statusChannel, runnerSecret);
        var commandClient = CreateAuthenticatedClient(commandChannel, runnerSecret);
        var sessionEventClient = CreateAuthenticatedClient(sessionEventChannel, runnerSecret);
        var opsClient = CreateAuthenticatedClient(opsChannel, runnerSecret);

        await using var sessionSink = new GrpcRunnerControlSink(
            statusClient,
            commandClient,
            sessionEventClient,
            opsClient,
            rpcTimeout);

        var initialStatusApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandWorkState = new RunnerCommandWorkState(shutdown);
        sessionEventRelay.AttachSink(sessionSink);
        sessionProcessLogRelay.AttachSink(sessionSink);
        runnerLogRelay.AttachSink(sessionSink);
        sessionRuntimeTelemetryRelay.AttachSink(sessionSink);

        var initialLocalModelSnapshot = PrepareInitialLocalModelSnapshot(localModelSnapshotProvider, localModelSnapshotObserver);

        downloadService.ProgressReporter = async progress =>
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
                if (runtimeIdentities.TryGet(progress.SessionId, out var runtimeIdentity))
                {
                    request.LifecycleGeneration = runtimeIdentity.LifecycleGeneration;
                    request.RuntimeEpoch = runtimeIdentity.RuntimeEpoch;
                    request.RuntimeInstanceId = runtimeIdentity.RuntimeInstanceId;
                    reportedRuntime = runtimeIdentity;
                }
                await sessionSink.ReportModelDownloadAsync(request, CancellationToken.None).ConfigureAwait(false);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.PermissionDenied)
            {
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to report model download progress: {ex.Message}");
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
            catch (Exception ex)
            {
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to report model download progress: {ex.Message}");
            }
        };

        try
        {
            var statusTask = RunRunnerStatusLoopAsync(
                sessionSink,
                shutdown,
                runnerId,
                instanceId,
                version,
                logicalSessionState.GetActiveSessionId,
                resources,
                supportedEditorTemplates,
                minBackoff,
                maxBackoff,
                initialStatusApplied);
            var localModelTask = localModelSnapshotProvider is null
                ? Task.CompletedTask
                : RunLocalModelSyncLoopAsync(
                    sessionSink.SyncLocalModelsAsync,
                    shutdown,
                    runnerId,
                    localModelsScanInterval,
                    minBackoff,
                    maxBackoff,
                    localModelSnapshotProvider,
                    localModelSnapshotObserver,
                    initialStatusApplied.Task,
                    initialLocalModelSnapshot);
            var commandTask = RunCommandStreamLoopAsync(
                commandClient,
                sessionSink,
                shutdown,
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
                runtimeIdentities);

            await Task.WhenAll(statusTask, localModelTask, commandTask).ConfigureAwait(false);
        }
        finally
        {
            await gpuPowerLimitService.CancelAndDrainAsync().ConfigureAwait(false);
            await commandWorkState.Prompts.CancelAndWaitAsync(_ => true).ConfigureAwait(false);
            await CancelTrackedWorkAsync(commandWorkState.GetActiveStartup()).ConfigureAwait(false);

            downloadService.ProgressReporter = null;
            downloadService.SetActiveSession(null);
            sessionEventRelay.DetachSink(sessionSink);
            sessionProcessLogRelay.DetachSink(sessionSink);
            runnerLogRelay.DetachSink(sessionSink);
            sessionRuntimeTelemetryRelay.DetachSink(sessionSink);
        }
    }

    private static async Task RunRunnerStatusLoopAsync(
        IRunnerSessionSink sessionSink,
        CancellationToken shutdown,
        string runnerId,
        string instanceId,
        string version,
        Func<string> getActiveSessionId,
        RunnerResources resources,
        IReadOnlyList<string> supportedEditorTemplates,
        TimeSpan minBackoff,
        TimeSpan maxBackoff,
        TaskCompletionSource initialStatusApplied)
    {
        ArgumentNullException.ThrowIfNull(sessionSink);

        var attempt = 0;
        var rng = new Random();
        var nextDelay = TimeSpan.Zero;

        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                if (nextDelay > TimeSpan.Zero)
                {
                    await Task.Delay(nextDelay, shutdown).ConfigureAwait(false);
                }

                var response = await sessionSink.RegisterRunnerAsync(
                    BuildStatusUpdateRequest(runnerId, instanceId, version, resources, getActiveSessionId, supportedEditorTemplates),
                    shutdown).ConfigureAwait(false);

                RunnerLog.Info(nameof(ServiceClientManager), $"Status update accepted={response.Accepted} message='{response.Message}' next={response.RefreshSeconds}s");
                if (!response.Accepted)
                {
                    throw new RunnerSessionRejectedException(response.Message);
                }

                initialStatusApplied.TrySetResult();
                attempt = 0;
                nextDelay = response.RefreshSeconds > 0
                    ? TimeSpan.FromSeconds(response.RefreshSeconds)
                    : TimeSpan.FromMinutes(1);
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                attempt++;
                nextDelay = RegistrationPolicy.ComputeBackoff(attempt, minBackoff, maxBackoff, rng.NextDouble());
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Runner status update failed attempt {attempt}: {ex.Message}");
                RunnerLog.Warning(nameof(ServiceClientManager), $"Retrying runner status update in {nextDelay.TotalSeconds:F1}s");
            }
        }
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
        Action<IEnumerable<ModelDownloadRequest>, bool>? localModelProjectionReconciler,
        RunnerCommandWorkState commandWorkState,
        LogicalSessionState logicalSessionState,
        RuntimeIdentityRegistry runtimeIdentities)
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
        var runtimeCommandDeduplication = new RuntimeCommandDeduplicationCache();
        var transientCancellation = new TransientCancellationRegistry(shutdown);
        using var ordinaryControlGate = new SemaphoreSlim(1, 1);
        using var readGate = new SemaphoreSlim(4, 4);
        var runtimeCommands = new RuntimeCommandDependencies
        {
            RunnerId = runnerId,
            Sink = sessionSink,
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
            StopSessionRelay = sessionEventRelay.StopSessionAsync,
            StopArtifactUploads = artifactUploadService.StopSessionAsync,
            StopWorkspaceMirrors = workspaceMirrorService.StopSessionAsync,
            CancelSessionDownloads = downloadService.CancelSession,
            GetDownloadActiveSessionId = downloadService.GetActiveSessionId,
            SetDownloadActiveSession = downloadService.SetActiveSession,
            ActivateSession = gpuPowerLimitService.ActivateSessionAsync,
            Shutdown = shutdown,
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
                        InstanceId = instanceId
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

                    if (evt.StartSession != null && !string.IsNullOrWhiteSpace(evt.StartSession.SessionId))
                    {
                        var startCommandKey = $"start:{evt.StartSession.SessionId}:{evt.StartSession.LifecycleGeneration}:{evt.StartSession.RuntimeEpoch}:{evt.StartSession.RuntimeInstanceId}";
                        if (!runtimeCommandDeduplication.TryAdd(startCommandKey))
                        {
                            continue;
                        }

                        RunnerLog.Info(nameof(ServiceClientManager), $"StartSession event: session={evt.StartSession.SessionId} user={evt.StartSession.User}");
                        _ = HandleStartSessionCommand(evt.StartSession, runtimeCommands);
                        continue;
                    }

                    if (evt.QuiesceEditorRuntime != null && !string.IsNullOrWhiteSpace(evt.QuiesceEditorRuntime.CommandId))
                    {
                        if (!runtimeCommandDeduplication.TryAdd(evt.QuiesceEditorRuntime.CommandId))
                        {
                            continue;
                        }

                        _ = HandleQuiesceRuntimeCommandAsync(evt.QuiesceEditorRuntime, runtimeCommands);
                        continue;
                    }

                    if (evt.LaunchEditorRuntime != null && !string.IsNullOrWhiteSpace(evt.LaunchEditorRuntime.CommandId))
                    {
                        if (!runtimeCommandDeduplication.TryAdd(evt.LaunchEditorRuntime.CommandId))
                        {
                            continue;
                        }

                        _ = HandleLaunchRuntimeCommand(evt.LaunchEditorRuntime, runtimeCommands);
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
                        _ = gpuPowerLimitService.HandleAsync(
                            evt.SetGpuPowerLimit, runnerId, logicalSessionState.GetActiveSessionId,
                            () => runtimeIdentities.TryGet(evt.SetGpuPowerLimit.SessionId, out var identity) ? identity.LifecycleGeneration : 0,
                            sessionSink.GrantGpuPowerExecutionAsync, sessionSink.AckGpuPowerLimitAsync, shutdown);
                        continue;
                    }

                    if (evt.RelayEditorRuntimeRequest != null && !string.IsNullOrWhiteSpace(evt.RelayEditorRuntimeRequest.RequestId))
                    {
                        var runtimeRequest = evt.RelayEditorRuntimeRequest;
                        _ = DispatchTransientEditorRuntimeRequest(
                            runtimeRequest,
                            runnerId,
                            transientDeliveries,
                            transientCancellation,
                            () =>
                            {
                                if (runtimeRequest.Kind == EditorRuntimeRequestKind.Control &&
                                    ComfyRuntimeRoutes.IsGlobalInterrupt(runtimeRequest.Path, runtimeRequest.Body.Span))
                                {
                                    commandWorkState.Prompts.Cancel(prompt =>
                                        TargetsPromptRuntime(runtimeRequest, prompt) && !ShouldStreamHttpResponse(prompt));
                                }
                            },
                            (request, token) => ExecuteTransientEditorRuntimeRequestAsync(
                                request,
                                runnerId,
                                sessionManager,
                                sessionEventRelay,
                                ordinaryControlGate,
                                readGate,
                                token),
                            sessionSink.AckEditorRuntimeRequestAsync,
                            shutdown);
                        continue;
                    }

                    if (evt.SubmitPrompt != null && !string.IsNullOrWhiteSpace(evt.SubmitPrompt.SessionId))
                    {
                        var prompt = evt.SubmitPrompt;
                        commandWorkState.Prompts.Start(prompt, (captured, physicalCancellation) =>
                        {
                            return HandleSubmitEditorActionAsync(
                            (ack, acknowledgementCancellation) =>
                            {
                                // Delivery retries retain this exact result/identity and
                                // run independently from the physical work joined by Stop.
                                _ = AcknowledgeRuntimeCommandWithRetryAsync(
                                    sessionSink.AckSubmitEditorActionAsync, ack, captured.CommandId, shutdown);
                                return Task.CompletedTask;
                            },
                            (events, token) => sessionSink.ReportSessionEventsAsync(events, token),
                            captured,
                            runnerId,
                            sessionSink.GrantPromptExecutionAsync,
                            sessionSink.ReportPromptEvidenceAsync,
                            (command, token) => sessionManager.RecoverPromptAsync(command, runnerId, sessionSink.ReportPromptEvidenceAsync, token),
                            (sessionId, editorSid, clientId, token) => sessionEventRelay.EnsureBridgeAsync(
                                sessionId,
                                editorSid,
                                clientId,
                                captured.Target.GpuGeneration,
                                captured.Target.RuntimeEpoch,
                                captured.Target.RuntimeInstanceId,
                                captured.WebsocketClientMessage.ToByteArray(),
                                token),
                            (sessionId, clientId, submissionId) => sessionEventRelay.RegisterSubmission(sessionId, clientId, captured),
                            downloadService.SetActiveSession,
                            (sessionId, editorSid, owner, token) => artifactUploadService.ActivateSessionAsync(sessionId, editorSid, owner, token),
                            (sessionId, editorSid, owner, artifacts, token) => artifactUploadService.SeedSessionAsync(sessionId, editorSid, owner, artifacts, token),
                            artifactUploadService.CaptureSessionRefresh,
                            downloadService.RegisterDownloads,
                            (downloads, token, highPriority) => downloadService.EnsureDownloadsAsync(downloads, token, highPriority),
                            (submitPromptCommand, responseChunkSink, onNativeRequestAttempt, token) => sessionManager.SubmitEditorActionAsync(submitPromptCommand, responseChunkSink, token, onNativeRequestAttempt),
                            (sessionId, clientId, promptId, _) => sessionEventRelay.MapPrompt(sessionId, clientId, captured, promptId),
                            physicalCancellation,
                            localModelProjectionReconciler,
                            sessionId => sessionManager.TryGetSessionPaths(sessionId, out var paths) ? paths : null,
                            shutdown,
                            sessionEventRelay.UnregisterUninvokedSubmission);
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

        transientCancellation.CancelAll();
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

        await stopSession(
            activeSessionId,
            (sessionId, token) => StopArtifactAndWorkspaceMirrorsAsync(sessionId, stopArtifactUploads, stopWorkspaceMirrors, token),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await stopSessionRelay(activeSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop websocket relay for preempted session {activeSessionId}");
        }

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
                    revoked.LifecycleGeneration,
                    revoked.RuntimeEpoch,
                    revoked.RuntimeInstanceId);
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
            await StopArtifactAndWorkspaceMirrorsAsync(normalizedSessionId, stopArtifactUploads, stopWorkspaceMirrors, CancellationToken.None).ConfigureAwait(false);
            try
            {
                await stopSessionRelay(normalizedSessionId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop websocket relay for revoked session {normalizedSessionId}");
            }

            return false;
        }

        var sidecarsStopped = false;
        async Task StopSidecarsOnceAsync(string sid, CancellationToken token)
        {
            if (sidecarsStopped)
            {
                return;
            }

            await StopArtifactAndWorkspaceMirrorsAsync(sid, stopArtifactUploads, stopWorkspaceMirrors, token).ConfigureAwait(false);
            sidecarsStopped = true;
        }

        try
        {
            await stopSession(
                normalizedSessionId,
                StopSidecarsOnceAsync,
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await StopSidecarsOnceAsync(normalizedSessionId, CancellationToken.None).ConfigureAwait(false);
            // A native open that was already in flight can publish its descriptor
            // lease after the pre-stop cancellation. Once the child is gone, run
            // the idempotent cleanup again so no leaked lease or sticky demand remains.
            cancelSessionDownloads(normalizedSessionId);
        }

        try
        {
            await stopSessionRelay(normalizedSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop websocket relay for revoked session {normalizedSessionId}");
        }

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

        var sidecarsStopped = false;
        async Task StopSidecarsOnceAsync(string sid, CancellationToken token)
        {
            if (sidecarsStopped)
            {
                return;
            }

            await StopArtifactAndWorkspaceMirrorsAsync(sid, stopArtifactUploads, stopWorkspaceMirrors, token).ConfigureAwait(false);
            sidecarsStopped = true;
        }

        ExceptionDispatchInfo? stopFailure = null;
        try
        {
            await stopSession(
                normalizedSessionId,
                StopSidecarsOnceAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop session {normalizedSessionId}: {ex.Message}");
            stopFailure = ExceptionDispatchInfo.Capture(ex);
        }
        finally
        {
            await StopSidecarsOnceAsync(normalizedSessionId, CancellationToken.None).ConfigureAwait(false);
            // A native open that was already in flight can publish its descriptor
            // lease after the pre-stop cancellation. Once the child is gone, run
            // the idempotent cleanup again so no leaked lease or sticky demand remains.
            cancelSessionDownloads(normalizedSessionId);
        }

        try
        {
            await stopSessionRelay(normalizedSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop session websocket relay {normalizedSessionId}: {ex.Message}");
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
        var supersededInSession = logicalSessionState.TryGetRuntime(sessionId, out var logical, out _) &&
                                  !logical.SameRuntime(exitedRuntime);
        if (supersededInRegistry || supersededInSession)
        {
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Exited runtime {exitedRuntime.RuntimeInstanceId} of session '{sessionId}' was already replaced; leaving the newer runtime's state and sidecars.");
            return;
        }

        ClearRuntimeSessionStateIfIdentityMatches(
            logicalSessionState,
            runtimeIdentities,
            sessionId,
            exitedRuntime.LifecycleGeneration,
            exitedRuntime.RuntimeEpoch,
            exitedRuntime.RuntimeInstanceId);
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

        await StopArtifactAndWorkspaceMirrorsAsync(
            normalizedSessionId,
            stopArtifactUploads,
            stopWorkspaceMirrors,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await stopSessionRelay(normalizedSessionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop websocket relay for exited session {normalizedSessionId}: {ex.Message}");
        }
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
        ulong lifecycleGeneration,
        ulong runtimeEpoch,
        string? runtimeInstanceId)
    {
        logicalSessionState.ClearIfMatches(
            sessionId,
            lifecycleGeneration,
            runtimeEpoch,
            runtimeInstanceId);
        runtimeIdentities.RemoveIfMatches(
            sessionId,
            lifecycleGeneration,
            runtimeEpoch,
            runtimeInstanceId);
    }

    internal static async Task StopArtifactAndWorkspaceMirrorsAsync(
        string sessionId,
        Func<string, Task> stopArtifactUploads,
        Func<string, Task> stopWorkspaceMirrors,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        // Stop must always complete: a mirror that does not stop in time is logged
        // and left behind rather than blocking session cleanup.
        try
        {
            await stopArtifactUploads(sessionId).WaitAsync(MirrorStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RunnerLog.Error(nameof(ServiceClientManager), $"Artifact uploader for session {sessionId} did not stop within {MirrorStopTimeout.TotalSeconds:F0}s; continuing");
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop artifact uploader for session {sessionId}");
        }

        try
        {
            await stopWorkspaceMirrors(sessionId).WaitAsync(MirrorStopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            RunnerLog.Error(nameof(ServiceClientManager), $"Workspace mirror for session {sessionId} did not stop within {MirrorStopTimeout.TotalSeconds:F0}s; continuing");
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to stop workspace mirror for session {sessionId}");
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
    /// Cleans up after a failed start or launch of exactly <paramref name="failedRuntime"/>.
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
                failedRuntime.LifecycleGeneration,
                failedRuntime.RuntimeEpoch,
                failedRuntime.RuntimeInstanceId);
            return;
        }

        if (deps.GetTrackedRuntime(sessionId) is { } running &&
            deps.LogicalSessionState.ReplaceRuntimeIfMatches(sessionId, failedRuntime, running) &&
            deps.RuntimeIdentities.RemoveIfMatches(
                sessionId,
                failedRuntime.LifecycleGeneration,
                failedRuntime.RuntimeEpoch,
                failedRuntime.RuntimeInstanceId))
        {
            deps.RuntimeIdentities.Set(sessionId, running.LifecycleGeneration, running.RuntimeEpoch, running.RuntimeInstanceId);
        }
    }

    /// <summary>
    /// Handles Stop on the command stream. Physical cleanup is awaited; the
    /// acknowledgement is retried detached with the identical request, so a lost
    /// acknowledgement neither blocks later commands nor is dropped.
    /// </summary>
    internal static async Task HandleStopSessionCommandAsync(StopSessionCommand command, RuntimeCommandDependencies deps)
    {
        var ack = new AcknowledgeSessionStopRequest
        {
            SessionId = command.SessionId,
            User = command.User,
            CommandId = command.CommandId,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
        };
        var acknowledgementKey = string.IsNullOrWhiteSpace(command.CommandId) ? command.SessionId : command.CommandId;

        if (IsStopSuperseded(command, deps))
        {
            // A delayed or redelivered Stop for an older runtime must not stop the
            // newer one. The runtime it names is no longer running here.
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Ignoring stale stop for session {command.SessionId}; a newer runtime is current.");
            _ = AcknowledgeRuntimeCommandWithRetryAsync(deps.Sink.AckStopSessionAsync, ack, acknowledgementKey, deps.Shutdown);
            return;
        }

        await deps.WorkState.Prompts.CancelAndWaitAsync(
            prompt => prompt.SessionId == command.SessionId).ConfigureAwait(false);

        var activeStartup = deps.WorkState.GetActiveStartup();
        if (activeStartup is not null &&
            string.Equals(activeStartup.SessionId, command.SessionId, StringComparison.Ordinal))
        {
            await CancelTrackedWorkAsync(activeStartup).ConfigureAwait(false);
        }

        deps.TransientCancellation.CancelSession(command.SessionId);

        try
        {
            await StopSessionAndCleanupAsync(command.SessionId, deps, deps.Shutdown).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Keep the heartbeat and identity of a child that may still be alive,
            // and do not acknowledge: the control plane retains the Stop.
            RunnerLog.Error(nameof(ServiceClientManager), ex, $"Stop of session {command.SessionId} did not complete; retaining its state.");
            return;
        }

        deps.LogicalSessionState.ClearAndRetire(command.SessionId, command.LifecycleGeneration);
        deps.RuntimeIdentities.Remove(command.SessionId);
        _ = AcknowledgeRuntimeCommandWithRetryAsync(deps.Sink.AckStopSessionAsync, ack, acknowledgementKey, deps.Shutdown);
    }

    /// <summary>
    /// A Stop that carries a lifecycle generation is fenced: it is superseded when
    /// the session's current runtime, tracked child or active startup is newer.
    /// A Stop without one (an older control plane) is never fenced.
    /// </summary>
    private static bool IsStopSuperseded(StopSessionCommand command, RuntimeCommandDependencies deps)
    {
        if (command.LifecycleGeneration == 0)
        {
            return false;
        }

        if (deps.LogicalSessionState.TryGetRuntime(command.SessionId, out var logical, out _) &&
            IsNewerThanStop(logical, command))
        {
            return true;
        }

        if (deps.GetTrackedRuntime(command.SessionId) is { } tracked && IsNewerThanStop(tracked, command))
        {
            return true;
        }

        return deps.WorkState.GetActiveStartup() is { } startup &&
               string.Equals(startup.SessionId, command.SessionId, StringComparison.Ordinal) &&
               IsNewerThanStop(startup.RuntimeIdentity, command);
    }

    private static bool IsNewerThanStop(RuntimeIdentity current, StopSessionCommand stop)
    {
        if (!current.IsValid)
        {
            return false;
        }

        if (current.LifecycleGeneration != stop.LifecycleGeneration)
        {
            return current.LifecycleGeneration > stop.LifecycleGeneration;
        }

        if (stop.RuntimeEpoch == 0)
        {
            return false;
        }

        if (current.RuntimeEpoch != stop.RuntimeEpoch)
        {
            return current.RuntimeEpoch > stop.RuntimeEpoch;
        }

        // Same position, different instance: the Stop names a runtime that is
        // not this one, so it must not stop it.
        return !string.IsNullOrWhiteSpace(stop.RuntimeInstanceId) &&
               !current.SameRuntime(new RuntimeIdentity(stop.LifecycleGeneration, stop.RuntimeEpoch, stop.RuntimeInstanceId));
    }

    internal static Task HandleStartSessionCommand(StartSessionCommand command, RuntimeCommandDependencies deps)
    {
        var runtimeIdentity = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId.Trim());
        var ack = new AcknowledgeSessionStartRequest
        {
            SessionId = command.SessionId,
            User = command.User,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
        };

        // A delayed start for an older runtime of this session, for a session that already
        // ended here, or from an older assignment must not cancel or replace newer work.
        // These checks run before any tracked startup or prompt is cancelled.
        if (deps.LogicalSessionState.IsSuperseded(command.SessionId, runtimeIdentity, restartRevision: 0) ||
            deps.LogicalSessionState.IsStaleStart(command.SessionId, runtimeIdentity, command.AssignmentSequence))
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Rejecting stale start for session {command.SessionId}.");
            ack.Ready = false;
            ack.Message = StaleRuntimeCommandMessage;
            return AcknowledgeRuntimeCommandWithRetryAsync(
                deps.Sink.AckStartSessionAsync,
                ack,
                command.SessionId,
                deps.Shutdown);
        }

        deps.LogicalSessionState.ObserveAssignment(command.AssignmentSequence);

        return RunTrackedStartupAsync(
            deps,
            command.SessionId,
            runtimeIdentity,
            async cancellationToken =>
            {
                try
                {
                    await deps.WorkState.Prompts.CancelAndWaitAsync(
                        prompt => prompt.SessionId != command.SessionId).ConfigureAwait(false);

                    // This wait belongs to cancellable startup work, never the
                    // command stream: Stop must still preempt a queued replacement.
                    await deps.ActivateSession(() =>
                    {
                        if (!deps.LogicalSessionState.TryAdoptRuntime(
                                command.SessionId,
                                runtimeIdentity,
                                restartRevision: 0,
                                replaceOtherSession: true,
                                command.AssignmentSequence))
                        {
                            throw new InvalidOperationException(StaleRuntimeCommandMessage);
                        }

                        deps.RuntimeIdentities.Set(command.SessionId, command.LifecycleGeneration,
                            command.RuntimeEpoch, command.RuntimeInstanceId);
                    }, cancellationToken).ConfigureAwait(false);

                    var preemptedSessionId = await PreemptActiveSessionIfNeededAsync(
                        command.SessionId,
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
                    await deps.StartRuntimeAndWaitForReady(command, cancellationToken).ConfigureAwait(false);
                    ack.Ready = true;
                    ack.Message = IsComfyRuntime(command.EditorRuntimeKind) ? "ComfyUI ready" : "GPU runtime ready";
                }
                catch (Exception ex)
                {
                    RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to start session {command.SessionId}: {ex.Message}");
                    ack.Ready = false;
                    ack.Message = ex.Message;
                    await CleanUpFailedStartupAsync(command.SessionId, runtimeIdentity, deps).ConfigureAwait(false);
                }

                return ack;
            },
            deps.Sink.AckStartSessionAsync,
            command.SessionId);
    }

    internal static async Task HandleQuiesceRuntimeCommandAsync(
        QuiesceEditorRuntimeCommand command,
        RuntimeCommandDependencies deps)
    {
        var ack = new AcknowledgeEditorRuntimeQuiesceRequest
        {
            CommandId = command.CommandId,
            RestartId = command.RestartId,
            RunnerId = deps.RunnerId,
            SessionId = command.SessionId,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
            RestartRevision = command.RestartRevision,
        };
        var target = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId.Trim());
        if (!RunnerIdMatches(command.RunnerId, deps.RunnerId) || !IsQuiesceTargetCurrent(command, target, deps))
        {
            // A delayed quiesce for an older runtime must not touch a newer one.
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Rejecting stale or foreign quiesce {command.CommandId} for session {command.SessionId}.");
            ack.Quiesced = false;
            ack.Message = StaleRuntimeCommandMessage;
        }
        else
        {
            try
            {
                var activeStartup = deps.WorkState.GetActiveStartup();
                if (activeStartup is not null &&
                    string.Equals(activeStartup.SessionId, command.SessionId, StringComparison.Ordinal) &&
                    activeStartup.RuntimeIdentity.SameRuntime(target))
                {
                    await CancelTrackedWorkAsync(activeStartup).ConfigureAwait(false);
                }

                await deps.WorkState.Prompts.CancelAndWaitAsync(
                    prompt => PromptTargetsRuntime(prompt, command.SessionId, target)).ConfigureAwait(false);
                deps.TransientCancellation.CancelRuntime(command.SessionId, target);

                if (!await StopRuntimeAndCleanupIfCurrentAsync(command.SessionId, target, deps, deps.Shutdown).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(StaleRuntimeCommandMessage);
                }

                ack.Quiesced = true;
                ack.Message = "GPU child runtime quiesced";
            }
            catch (Exception ex)
            {
                ack.Quiesced = false;
                ack.Message = ex.Message;
                RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed quiescing runtime for restart {command.RestartId}: {ex.Message}");
            }
        }

        await AcknowledgeRuntimeCommandWithRetryAsync(
            deps.Sink.AckEditorRuntimeQuiesceAsync,
            ack,
            command.CommandId,
            deps.Shutdown).ConfigureAwait(false);
    }

    /// <summary>
    /// A quiesce applies only to the session's current runtime: the logical
    /// identity when one is held, otherwise the tracked child, if any.
    /// </summary>
    private static bool IsQuiesceTargetCurrent(
        QuiesceEditorRuntimeCommand command,
        RuntimeIdentity target,
        RuntimeCommandDependencies deps)
    {
        if (!target.IsValid)
        {
            return false;
        }

        // Only starts admitted as newer reach the startup slot, so a pending startup for a
        // different runtime of this session means the quiesce is stale, even while that
        // startup has not adopted its identity yet and no child is tracked.
        if (deps.WorkState.GetActiveStartup() is { } pending &&
            string.Equals(pending.SessionId, command.SessionId, StringComparison.Ordinal) &&
            !pending.RuntimeIdentity.SameRuntime(target))
        {
            return false;
        }

        if (deps.LogicalSessionState.TryGetRuntime(command.SessionId, out var current, out var currentRevision))
        {
            return current.SameRuntime(target) && command.RestartRevision >= currentRevision;
        }

        return deps.GetTrackedRuntime(command.SessionId) is not { } tracked || tracked.SameRuntime(target);
    }

    private static bool PromptTargetsRuntime(SubmitPromptCommand prompt, string sessionId, RuntimeIdentity runtimeIdentity)
        => string.Equals(prompt.SessionId, sessionId, StringComparison.Ordinal) &&
           prompt.Target is { } target &&
           new RuntimeIdentity(target.GpuGeneration, target.RuntimeEpoch, target.RuntimeInstanceId).SameRuntime(runtimeIdentity);

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

    internal static Task HandleLaunchRuntimeCommand(
        LaunchEditorRuntimeCommand command,
        RuntimeCommandDependencies deps)
    {
        var runtimeIdentity = new RuntimeIdentity(command.LifecycleGeneration, command.RuntimeEpoch, command.RuntimeInstanceId.Trim());
        var ack = new AcknowledgeEditorRuntimeLaunchRequest
        {
            CommandId = command.CommandId,
            RestartId = command.RestartId,
            RunnerId = deps.RunnerId,
            SessionId = command.SessionId,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
            RestartRevision = command.RestartRevision,
            Attempt = command.Attempt,
        };

        // Adopt the identity on receipt, but only for this runner and only when
        // it is not older than the session's current runtime. A delayed launch
        // must neither replace the heartbeat identity nor cancel newer startup.
        if (!RunnerIdMatches(command.RunnerId, deps.RunnerId) ||
            !deps.LogicalSessionState.TryAdoptRuntime(
                command.SessionId,
                runtimeIdentity,
                command.RestartRevision,
                replaceOtherSession: false))
        {
            RunnerLog.Warning(
                nameof(ServiceClientManager),
                $"Rejecting stale or foreign launch {command.CommandId} for session {command.SessionId}.");
            ack.Ready = false;
            ack.Message = StaleRuntimeCommandMessage;
            return AcknowledgeRuntimeCommandWithRetryAsync(
                deps.Sink.AckEditorRuntimeLaunchAsync,
                ack,
                command.CommandId,
                deps.Shutdown);
        }

        deps.RuntimeIdentities.Set(
            command.SessionId,
            command.LifecycleGeneration,
            command.RuntimeEpoch,
            command.RuntimeInstanceId);
        var start = new StartSessionCommand
        {
            SessionId = command.SessionId,
            User = command.User,
            EditorSid = command.EditorSid,
            EditorTemplate = command.EditorTemplate,
            EditorRuntimeKind = command.EditorRuntimeKind,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
        };
        start.InitialModels.AddRange(command.InitialModels);
        start.InitialArtifacts.AddRange(command.InitialArtifacts);
        start.InitialWorkspaceFiles.AddRange(command.InitialWorkspaceFiles);

        return RunTrackedStartupAsync(
            deps,
            command.SessionId,
            runtimeIdentity,
            async cancellationToken =>
            {
                try
                {
                    await deps.StartRuntimeAndWaitForReady(start, cancellationToken).ConfigureAwait(false);
                    ack.Ready = true;
                    ack.Message = IsComfyRuntime(command.EditorRuntimeKind) ? "ComfyUI ready" : "GPU runtime ready";
                }
                catch (Exception ex)
                {
                    ack.Ready = false;
                    ack.Message = ex.Message;
                    RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed launching runtime for restart {command.RestartId}: {ex.Message}");
                    // Clean up only this launch's runtime, never a valid runtime of
                    // another identity that happens to share the session id.
                    await CleanUpFailedStartupAsync(command.SessionId, runtimeIdentity, deps).ConfigureAwait(false);
                }

                return ack;
            },
            deps.Sink.AckEditorRuntimeLaunchAsync,
            command.CommandId);
    }

    private const string StaleRuntimeCommandMessage = "The command targets a superseded runtime or another runner.";

    internal static bool RunnerIdMatches(string? commandRunnerId, string runnerId)
        => Guid.TryParse(commandRunnerId, out var commandRunner) && Guid.TryParse(runnerId, out var runner)
            ? commandRunner == runner
            : !string.IsNullOrWhiteSpace(commandRunnerId) &&
              string.Equals(commandRunnerId.Trim(), runnerId?.Trim(), StringComparison.Ordinal);

    /// <summary>
    /// Runs one startup in the single startup slot. The superseded startup is
    /// cancelled and awaited before this one touches the runtime, so two startups
    /// never overlap. The tracked task covers only the physical work; the
    /// acknowledgement retry runs after it, detached, so Stop never waits for it.
    /// </summary>
    private static Task RunTrackedStartupAsync<TAck>(
        RuntimeCommandDependencies deps,
        string sessionId,
        RuntimeIdentity runtimeIdentity,
        Func<CancellationToken, Task<TAck>> startup,
        Func<TAck, CancellationToken, Task> acknowledge,
        string acknowledgementKey)
    {
        // A redelivery of the startup already in progress joins it: replacing it would cancel
        // the work starting this very runtime, and that work's failure cleanup would stop it.
        // The running startup acknowledges with the same command and identity.
        if (deps.WorkState.GetActiveStartup() is { Task: { } inProgress } active &&
            string.Equals(active.SessionId, sessionId, StringComparison.Ordinal) &&
            active.RuntimeIdentity.SameRuntime(runtimeIdentity))
        {
            RunnerLog.Info(
                nameof(ServiceClientManager),
                $"Startup {acknowledgementKey} for session {sessionId} is already in progress; joining it.");
            return inProgress.ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        TrackedCommandWork? previous = null;
        var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(deps.Shutdown);
        var tracked = new TrackedCommandWork
        {
            CancellationSource = cancellationSource,
            SessionId = sessionId,
            RuntimeIdentity = runtimeIdentity,
        };

        // Publish a joinable task before it starts, so Stop and Quiesce can never
        // observe the slot without the work that owns it.
        var starter = new Task<Task<TAck>>(RunPhysicalAsync);
        var physical = starter.Unwrap();
        tracked.Task = physical;
        previous = deps.WorkState.ReplaceActiveStartup(tracked);
        starter.Start(TaskScheduler.Default);
        return AcknowledgeAsync();

        async Task<TAck> RunPhysicalAsync()
        {
            try
            {
                await CancelTrackedWorkAsync(previous).ConfigureAwait(false);
                return await startup(cancellationSource.Token).ConfigureAwait(false);
            }
            finally
            {
                deps.WorkState.ClearActiveStartup(tracked);
                cancellationSource.Dispose();
            }
        }

        async Task AcknowledgeAsync()
        {
            var ack = await physical.ConfigureAwait(false);
            await AcknowledgeRuntimeCommandWithRetryAsync(
                acknowledge,
                ack,
                acknowledgementKey,
                deps.Shutdown).ConfigureAwait(false);
        }
    }

    private static bool IsComfyRuntime(string? runtimeKind)
        => string.IsNullOrWhiteSpace(runtimeKind) ||
           string.Equals(runtimeKind.Trim(), "comfyui", StringComparison.OrdinalIgnoreCase);

    private static async Task StartSessionAndWaitForReadyAsync(
        StartSessionCommand command,
        SessionProcessManager sessionManager,
        ArtifactUploadService artifactUploadService,
        WorkspaceMirrorService workspaceMirrorService,
        ModelDownloadService downloadService,
        Action<IEnumerable<ModelDownloadRequest>, bool>? localModelProjectionReconciler,
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
                localModelProjectionReconciler?.Invoke(initialModels, true);
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

        if (work.Task is null)
        {
            work.Cancel();
            return;
        }

        work.Cancel();
        try
        {
            await work.Task.ConfigureAwait(false);
        }
        catch
        {
            // Prompt/start failures are logged and handled by their owning code path.
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
        Action beforeExecute,
        Func<RelayEditorRuntimeRequestCommand, CancellationToken, Task<AcknowledgeEditorRuntimeRequestRequest>> execute,
        Func<AcknowledgeEditorRuntimeRequestRequest, CancellationToken, Task> acknowledge,
        CancellationToken acknowledgementCancellationToken)
    {
        // A request for another runner must not reach this runner's editor, nor cancel its
        // prompts before execution, even if it names a session that exists here.
        if (!RunnerIdMatches(request.RunnerId, runnerId))
        {
            await AcknowledgeTransientRequestWithRetryAsync(
                acknowledge,
                TransientRequestFailure(request, 403, StaleRuntimeCommandMessage),
                acknowledgementCancellationToken).ConfigureAwait(false);
            return;
        }

        switch (deliveries.Admit(request, out var replay))
        {
            case TransientRequestAdmission.Replay:
                await AcknowledgeTransientRequestWithRetryAsync(acknowledge, replay!, acknowledgementCancellationToken).ConfigureAwait(false);
                return;
            case TransientRequestAdmission.Joined:
                return;
            case TransientRequestAdmission.AlreadyExecuted:
                await AcknowledgeTransientRequestWithRetryAsync(
                    acknowledge,
                    TransientRequestFailure(request, 409, "The request already ran on this runner; its result is no longer retained."),
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
            beforeExecute();
            using var lease = cancellation.Acquire(
                request.SessionId,
                request.LifecycleGeneration,
                request.RuntimeEpoch,
                request.RuntimeInstanceId);
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
                LifecycleGeneration = request.LifecycleGeneration,
                RuntimeEpoch = request.RuntimeEpoch,
                RuntimeInstanceId = request.RuntimeInstanceId,
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
            LifecycleGeneration = request.LifecycleGeneration,
            RuntimeEpoch = request.RuntimeEpoch,
            RuntimeInstanceId = request.RuntimeInstanceId,
            Kind = request.Kind,
            TransportSucceeded = false,
            StatusCode = statusCode,
            ErrorMessage = message,
        };

    private static async Task<AcknowledgeEditorRuntimeRequestRequest> ExecuteTransientEditorRuntimeRequestAsync(
        RelayEditorRuntimeRequestCommand command,
        string runnerId,
        SessionProcessManager sessionManager,
        ComfySessionEventRelay sessionEventRelay,
        SemaphoreSlim ordinaryControlGate,
        SemaphoreSlim readGate,
        CancellationToken cancellationToken)
    {
        var response = new AcknowledgeEditorRuntimeRequestRequest
        {
            RequestId = command.RequestId,
            DeliveryId = command.DeliveryId,
            RunnerId = runnerId,
            SessionId = command.SessionId,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
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

            if (command.Kind == EditorRuntimeRequestKind.ClientMessage)
            {
                await sessionEventRelay.SendClientMessageAsync(
                    command.SessionId,
                    command.EditorSid,
                    command.ClientId,
                    command.LifecycleGeneration,
                    command.RuntimeEpoch,
                    command.RuntimeInstanceId,
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

    internal static async Task<bool> AcknowledgeRuntimeCommandWithRetryAsync<TRequest>(
        Func<TRequest, CancellationToken, Task> acknowledge,
        TRequest response,
        string commandId,
        CancellationToken cancellationToken,
        TimeSpan? initialRetryDelay = null,
        TimeSpan? maximumRetryDelay = null,
        int? maxAttempts = null)
    {
        if (maxAttempts is <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
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
                if (maxAttempts is { } limit && attempt >= limit)
                {
                    RunnerLog.Warning(nameof(ServiceClientManager), $"Runtime command acknowledgement {commandId} exhausted {limit} attempts; retained status requires recovery.");
                    return false;
                }
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

    internal static async Task HandleSubmitEditorActionAsync(
        Func<AcknowledgePromptResultRequest, CancellationToken, Task> ackSubmitPrompt,
        Func<ReportSessionEventsRequest, CancellationToken, Task> reportSessionEvents,
        SubmitPromptCommand prompt,
        string runnerId,
        Func<GrantPromptExecutionRequest, CancellationToken, Task<GrantPromptExecutionResponse>> grantExecution,
        Func<ReportPromptEvidenceRequest, CancellationToken, Task> reportEvidence,
        Func<SubmitPromptCommand, CancellationToken, Task> recover,
        Func<string, string, string, CancellationToken, Task> ensureBridge,
        Action<string, string, string> registerSubmission,
        Action<string?> setActiveDownloadSession,
        Func<string, string, string?, CancellationToken, Task> activateSessionUploads,
        Func<string, string, string?, IEnumerable<EditorArtifactMetadata>, CancellationToken, Task> seedSessionArtifacts,
        Func<string, Func<Task>> captureSessionArtifactRefresh,
        Action<string, IEnumerable<ModelDownloadRequest>> registerDownloads,
        Func<IEnumerable<ModelDownloadRequest>, CancellationToken, bool, Task> ensureDownloads,
        Func<SubmitPromptCommand, Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>?, Action, CancellationToken, Task<SessionProcessManager.PromptSubmissionResult>> submitPrompt,
        Action<string, string, string, string> mapPrompt,
        CancellationToken cancellationToken,
        Action<IEnumerable<ModelDownloadRequest>, bool>? localModelProjectionReconciler = null,
        Func<string, SessionProcessManager.SessionPaths?>? sessionPathsProvider = null,
        CancellationToken evidenceDeliveryCancellationToken = default,
        Action<string, string, SubmitPromptCommand>? unregisterUninvokedSubmission = null)
    {
        // Reject malformed wire identities before granting or touching a runtime.
        _ = PromptRuntimeMarker.TargetFingerprint(prompt.Target);
        if (!Guid.TryParse(prompt.CommandId, out var commandId) || commandId == Guid.Empty
            || !Guid.TryParse(prompt.SubmissionId, out var submissionId) || submissionId == Guid.Empty
            || !Guid.TryParse(runnerId, out var runner) || runner != Guid.Parse(prompt.Target.RunnerId)
            || !Guid.TryParse(prompt.SessionId, out var session) || session != Guid.Parse(prompt.Target.RunnerSessionId))
            throw new ArgumentException("The prompt command must retain its exact executor identity.", nameof(prompt));
        if (prompt.RecoveryOnly)
        {
            await recover(prompt, cancellationToken).ConfigureAwait(false);
            return;
        }
        var grant = new GrantPromptExecutionRequest
        {
            RunnerId = runnerId,
            SessionId = prompt.SessionId,
            SubmissionId = prompt.SubmissionId,
            CommandId = prompt.CommandId,
            Target = prompt.Target.Clone(),
        };
        GrantPromptExecutionResponse permission;
        var grantAttempt = 0;
        var grantRetryDelay = TimeSpan.FromMilliseconds(250);
        while (true)
        {
            grantAttempt++;
            try
            {
                permission = await grantExecution(grant, cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (IsRetryableTransientAcknowledgementFailure(ex))
            {
                // Only the first affirmative committed permission authorizes
                // execution. Retrying the same identity after a lost response
                // yields recovery-only, never another native invocation.
                if (grantAttempt == 1 || grantAttempt % 12 == 0)
                    RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt permission {prompt.CommandId} unavailable ({ex.GetType().Name}); retrying the same grant.");
                try { await Task.Delay(grantRetryDelay, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                grantRetryDelay = TimeSpan.FromMilliseconds(Math.Min(grantRetryDelay.TotalMilliseconds * 2, 5000));
            }
            catch (Exception ex)
            {
                RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt permission {prompt.CommandId} rejected ({ex.GetType().Name}); retaining its receipt for recovery.");
                return;
            }
        }
        if (permission.Submission is null || permission.Submission.SubmissionId != prompt.SubmissionId
            || !prompt.Target.Equals(permission.Submission.Target))
            throw new InvalidOperationException("Prompt permission returned a different submission identity.");
        if (!permission.MayExecute)
        {
            await recover(prompt, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (permission.Submission.ExecutionPermissionUtc is null)
            throw new InvalidOperationException("Prompt execution requires committed permission evidence.");

        var ack = new AcknowledgePromptResultRequest
        {
            SessionId = prompt.SessionId,
            SubmissionId = prompt.SubmissionId,
            RunnerId = runnerId,
            CommandId = prompt.CommandId,
            Target = prompt.Target.Clone(),
        };

        string? clientId = null;
        var nativeInvocationStarted = false;
        using var forgeEvidence = ShouldStreamHttpResponse(prompt)
            ? new ForgePromptEvidenceReader(prompt.NativeIdentifiers.SessionHash) : null;

        try
        {
            clientId = RegistrationPolicy.TryExtractClientId(prompt.PromptJson);
            if (!string.IsNullOrWhiteSpace(clientId))
            {
                await ensureBridge(prompt.SessionId, prompt.EditorSid, clientId!, cancellationToken).ConfigureAwait(false);
                registerSubmission(prompt.SessionId, clientId!, prompt.SubmissionId);
            }
        }
        catch (Exception ex)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt websocket preparation unavailable ({ex.GetType().Name}).");
        }

        try
        {
            // Capture the uploader under this command's permission, inside the
            // acknowledgement path. A missing uploader is proven pre-invocation
            // failure; a delayed sweep can never bind a replacement session.
            var capturedRefresh = captureSessionArtifactRefresh(prompt.SessionId);
            Task RefreshCapturedArtifacts(string _) => capturedRefresh();
            if (ShouldPrepareGenerationDependencies(prompt))
            {
                setActiveDownloadSession(prompt.SessionId);
                var downloads = MaterializeModelDownloadPathsForSession(
                    prompt.Downloads,
                    prompt.EditorRuntimeKind,
                    sessionPathsProvider?.Invoke(prompt.SessionId));
                localModelProjectionReconciler?.Invoke(downloads, false);
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

                await ensureDownloads(remoteDownloads, cancellationToken, false).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var result = await submitPrompt(
                prompt,
                ShouldStreamHttpResponse(prompt)
                    ? async (eventType, chunk, token) =>
                    {
                        if (eventType == HttpResponseChunkEventType && forgeEvidence is not null)
                            foreach (var evidence in forgeEvidence.Append(chunk.Span))
                                _ = ReportRetainedForgeEvidenceAsync(reportEvidence, new()
                                {
                                    RunnerId = runnerId,
                                    SessionId = prompt.SessionId,
                                    SubmissionId = prompt.SubmissionId,
                                    CommandId = prompt.CommandId,
                                    Target = prompt.Target.Clone(),
                                    Evidence = evidence,
                                }, evidenceDeliveryCancellationToken);
                        try
                        {
                            await ReportHttpResponseEventAsync(reportSessionEvents, runnerId, prompt, eventType, chunk, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            // Live fan-out loss cannot cancel an accepted consuming
                            // reader. Terminal evidence has its independent delivery.
                            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt live relay unavailable ({ex.GetType().Name}); use retained recovery evidence.");
                        }
                    }
            : null,
                () => nativeInvocationStarted = true,
                cancellationToken).ConfigureAwait(false);
            ack.Result = new()
            {
                Outcome = PromptInvocationOutcome.NativeResponse,
                NativeResponse = new() { StatusCode = result.StatusCode, Body = ByteString.CopyFrom(result.Body), ContentType = result.ContentType },
            };
            if (result.BodyOmitted)
            {
                ack.Result.DiagnosticCode = PromptProtocolCodes.NativeResponseBodyOmitted;
                ack.Result.Message = "The native response exceeded the retained size; its body was not kept.";
            }

            if (result.Success && !string.IsNullOrWhiteSpace(clientId))
            {
                var promptId = RegistrationPolicy.TryExtractPromptId(System.Text.Encoding.UTF8.GetString(result.Body));
                if (!string.IsNullOrWhiteSpace(promptId))
                {
                    mapPrompt(prompt.SessionId, clientId!, promptId!, prompt.SubmissionId);
                }
            }

            if (result.Success && ShouldRefreshArtifactsAfterPrompt(prompt))
            {
                KickOffPromptArtifactRefresh(prompt.SessionId, prompt.SubmissionId, RefreshCapturedArtifacts, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            if (!nativeInvocationStarted && !string.IsNullOrWhiteSpace(clientId))
            {
                try { unregisterUninvokedSubmission?.Invoke(prompt.SessionId, clientId, prompt); }
                catch (Exception cleanupEx)
                {
                    RunnerLog.Warning(nameof(ServiceClientManager), $"Uninvoked prompt registration cleanup failed ({cleanupEx.GetType().Name}).");
                }
            }
            RunnerLog.Warning(nameof(ServiceClientManager), nativeInvocationStarted
                ? $"Prompt {prompt.CommandId} ended with {ex.GetType().Name}; retaining invocation uncertainty."
                : $"Prompt {prompt.CommandId} ended with {ex.GetType().Name} before native request I/O.");
            ack.Result ??= new()
            {
                Outcome = nativeInvocationStarted ? PromptInvocationOutcome.Unknown : PromptInvocationOutcome.NotExecuted,
                DiagnosticCode = nativeInvocationStarted ? "prompt_native_result_unknown" : "prompt_preparation_incomplete",
                Message = nativeInvocationStarted ? "The runtime result could not be confirmed. Refresh the submission status."
                    : "Preparation ended before the native runtime was invoked.",
            };
        }

        // ackSubmitPrompt must retain this exact acknowledgement and retry it until it is
        // delivered or the host shuts down, independently of the prompt's own cancellation:
        // a redelivered prompt is denied execution and recovery cannot rebuild an arbitrary
        // native result. The command loop wires it to AcknowledgeRuntimeCommandWithRetryAsync
        // on the host shutdown token, so this only sees a failure to hand it over.
        try
        {
            var ackCancellationToken = cancellationToken.IsCancellationRequested
                ? CancellationToken.None
                : cancellationToken;
            await ackSubmitPrompt(ack, ackCancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Warning(nameof(ServiceClientManager), $"Prompt acknowledgement unavailable ({ex.GetType().Name}); recover its receipt.");
        }
    }

    internal static bool TargetsPromptRuntime(RelayEditorRuntimeRequestCommand request, SubmitPromptCommand prompt)
        => prompt.Target is { } target && request.LifecycleGeneration > 0 && request.RuntimeEpoch > 0
            && Guid.TryParse(request.SessionId, out var session) && session != Guid.Empty
            && Guid.TryParse(prompt.SessionId, out var promptSession) && session == promptSession
            && request.LifecycleGeneration == target.GpuGeneration && request.RuntimeEpoch == target.RuntimeEpoch
            && Guid.TryParse(request.RuntimeInstanceId, out var instance) && instance != Guid.Empty
            && Guid.TryParse(target.RuntimeInstanceId, out var promptInstance) && instance == promptInstance;

    private static Task ReportRetainedForgeEvidenceAsync(
        Func<ReportPromptEvidenceRequest, CancellationToken, Task> reportEvidence,
        ReportPromptEvidenceRequest retained,
        CancellationToken lifetime)
    {
        // The consuming native SSE reader and browser relay must never wait for
        // the evidence RPC. Retry only these captured bytes/identities, even if
        // Stop cancels the physical stream while this runner remains alive.
        return Task.Run(() => AcknowledgeRuntimeCommandWithRetryAsync(
            async (ReportPromptEvidenceRequest evidence, CancellationToken token) =>
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await reportEvidence(evidence, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("Prompt evidence reporting timed out.");
                }
            }, retained, retained.CommandId, lifetime, maxAttempts: 6));
    }

    private static bool ShouldStreamHttpResponse(SubmitPromptCommand prompt)
        => string.Equals(prompt.EditorRuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
           && string.Equals(prompt.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase)
           && PathMatchesRoute((prompt.HttpPath ?? string.Empty).Trim().TrimStart('/'), "queue/data");

    internal static bool IsSideEffectFreePromptRelay(SubmitPromptCommand prompt)
        => string.Equals(prompt.EditorRuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
           && string.Equals(prompt.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
           && PathMatchesRoute((prompt.HttpPath ?? string.Empty).Trim().TrimStart('/'), "internal/progress");

    private static bool ShouldRefreshArtifactsBeforePrompt(SubmitPromptCommand prompt)
        => ShouldPrepareGenerationDependencies(prompt)
           && string.Equals(prompt.EditorRuntimeKind, "comfyui", StringComparison.OrdinalIgnoreCase)
           && !string.IsNullOrWhiteSpace(prompt.EditorSid);

    internal static bool ShouldPrepareGenerationDependencies(SubmitPromptCommand prompt)
        => !string.Equals(prompt.EditorRuntimeKind, "comfyui", StringComparison.OrdinalIgnoreCase) ||
           ComfyRuntimeRoutes.Classify(prompt.HttpMethod, prompt.HttpPath) == ComfyRuntimeRouteOwnership.DurableGeneration;

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

        var rewritten = new ModelDownloadRequest[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            var download = snapshot[i];
            var clone = download.Clone();
            clone.DestinationPath = BuildSessionModelPath(modelsDirectory, download.RelativeModelPath, download.Filename);
            rewritten[i] = clone;
        }

        return rewritten;
    }

    private static string BuildSessionModelPath(string sessionModelsDirectory, string? relativeModelPath, string? filename)
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

        var modelsRoot = NormalizeFullPath(sessionModelsDirectory);
        var destinationPath = NormalizeFullPath(Path.Combine(modelsRoot, normalizedRelativePath));
        if (!IsPathWithinRoot(destinationPath, modelsRoot))
        {
            throw new InvalidOperationException($"Forge model download '{filename}' escapes the session model root.");
        }

        // The session user owns its models directory; refuse symlinked components so
        // root-run downloads and placeholders cannot be redirected out of it.
        ModelDestinationPolicy.ConfineToSessionRoot(modelsRoot, destinationPath);
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
            ClientId = $"http-relay:{prompt.SubmissionId}",
            SubmissionId = prompt.SubmissionId,
            EventType = string.IsNullOrWhiteSpace(eventType) ? HttpResponseChunkEventType : eventType,
            FrameType = SessionStreamFrameType.Binary,
            Payload = ByteString.CopyFrom(chunk.ToArray()),
            CreatedUtc = Timestamp.FromDateTime(DateTime.UtcNow),
            LifecycleGeneration = prompt.Target.GpuGeneration,
            RuntimeEpoch = prompt.Target.RuntimeEpoch,
            RuntimeInstanceId = prompt.Target.RuntimeInstanceId,
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
        string submissionId,
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
                    RunnerLog.Error(nameof(ServiceClientManager), ex, $"Failed to refresh artifacts after prompt {submissionId}: {ex.Message}");
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

    private static RegisterRunnerRequest BuildStatusUpdateRequest(
        string runnerId,
        string instanceId,
        string version,
        RunnerResources resources,
        Func<string> getActiveSessionId)
        => BuildStatusUpdateRequest(
            runnerId,
            instanceId,
            version,
            resources,
            getActiveSessionId,
            ["comfyui-latest", "comfyui-master"]);

    private static RegisterRunnerRequest BuildStatusUpdateRequest(
        string runnerId,
        string instanceId,
        string version,
        RunnerResources resources,
        Func<string> getActiveSessionId,
        IReadOnlyList<string> supportedEditorTemplates)
    {
        // Refresh dynamic metrics each call
        resources.SystemRamBytes = ResourceDetection.GetTotalRamBytes();

        var request = new RegisterRunnerRequest
        {
            RunnerId = runnerId,
            InstanceId = instanceId,
            ActiveSessionReported = true,
            ActiveSessionId = getActiveSessionId(),
            Version = version,
            Resources = resources
        };
        request.SupportedEditorTemplates.AddRange(supportedEditorTemplates);
        return request;
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

    private static RunnerControlService.RunnerControlServiceClient CreateAuthenticatedClient(
        GrpcChannel channel,
        string runnerSecret)
    {
        var callInvoker = channel.Intercept(new RunnerSecretClientInterceptor(runnerSecret));
        return new RunnerControlService.RunnerControlServiceClient(callInvoker);
    }

    private sealed class RunnerSessionRejectedException(string message)
        : Exception(string.IsNullOrWhiteSpace(message) ? "Runner session status update was rejected." : message);
}

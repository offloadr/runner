using Offloadr.Runner.Core;

namespace Offloadr.Runner.Linux;

internal static partial class ServiceClientManager
{
    /// <summary>Owns each request's original cancellation callbacks until its execution ends.</summary>
    internal sealed class TransientCancellationRegistry(CancellationToken shutdown) : IAsyncDisposable
    {
        internal readonly record struct Key(string SessionId, RuntimeIdentity Runtime);
        internal readonly record struct Scope(string? SessionId, RuntimeIdentity? Runtime)
        {
            public bool Matches(Key key) =>
                (SessionId is null || string.Equals(SessionId, key.SessionId, StringComparison.Ordinal)) &&
                (Runtime is null || Runtime == key.Runtime);
        }

        internal sealed class Lease : IAsyncDisposable
        {
            private readonly TransientCancellationRegistry _owner;
            private readonly OwnedCancellation _cancellation;
            private readonly object _gate = new();
            private readonly TaskCompletionSource<Task> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Task? _completion;

            public Lease(TransientCancellationRegistry owner, Key key, CancellationToken shutdown)
            {
                _owner = owner;
                Key = key;
                _cancellation = new(shutdown);
            }

            public Key Key { get; }
            public CancellationToken Token => _cancellation.Token;
            public bool IsCompleted => Volatile.Read(ref _completion)?.IsCompleted == true;
            public void RequestCancellation() => _ = _cancellation.RequestAsync();

            public ValueTask DisposeAsync()
            {
                TaskCompletionSource? start = null;
                Task completion;
                lock (_gate)
                {
                    if (_completion is null)
                    {
                        start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _completion = CompleteAsync(start.Task);
                        _published.SetResult(_completion);
                    }
                    completion = _completion;
                }
                // Completion can unregister a host callback or enter the owner;
                // neither operation runs under this publication gate.
                start?.SetResult();
                return new(completion);
            }

            public async Task JoinAsync()
                => await (await _published.Task.ConfigureAwait(false)).ConfigureAwait(false);

            private async Task CompleteAsync(Task start)
            {
                await start.ConfigureAwait(false);
                try
                {
                    await _cancellation.CompleteAsync().ConfigureAwait(false);
                    if (_cancellation.CallbackFailed)
                        _owner.NoteFailure(new InvalidOperationException("Transient cancellation callback failed."));
                }
                catch (Exception error) { _owner.NoteFailure(error); }
                finally { _owner.Release(this); }
            }
        }

        /// <summary>Keeps matching admission closed through its caller's physical cleanup.</summary>
        internal sealed class Drain(TransientCancellationRegistry owner, Scope scope) : IDisposable
        {
            public Scope Scope { get; } = scope;
            public Task Completion { get; private set; } = Task.CompletedTask;
            public void Start(Lease[] leases) => Completion = owner.JoinAsync(leases);
            public void Dispose() { lock (owner._gate) owner._blocks.Remove(this); }
        }

        private readonly object _gate = new();
        private readonly Dictionary<Key, HashSet<Lease>> _entries = [];
        private readonly HashSet<Lease> _owned = [];
        private readonly HashSet<Drain> _blocks = [];
        private Task? _close;
        private bool _closed;
        private bool _failed;

        public int Count { get { lock (_gate) return _entries.Count; } }

        public Lease Acquire(string? sessionId, ulong lifecycleGeneration, ulong runtimeEpoch, string? runtimeInstanceId)
        {
            var key = new Key(sessionId?.Trim() ?? string.Empty,
                new RuntimeIdentity(lifecycleGeneration, runtimeEpoch, runtimeInstanceId ?? string.Empty).Normalized());
            lock (_gate)
            {
                // Retain original completion roots until they actually finish,
                // even after removal from the active runtime map.
                _owned.RemoveWhere(lease => lease.IsCompleted);
                if (_closed || shutdown.IsCancellationRequested || _blocks.Any(drain => drain.Scope.Matches(key)))
                    throw new OperationCanceledException("Transient request admission is closed.", shutdown);
                var lease = new Lease(this, key, shutdown);
                if (!_entries.TryGetValue(key, out var leases)) _entries.Add(key, leases = []);
                leases.Add(lease);
                _owned.Add(lease);
                return lease;
            }
        }

        public void CancelSession(string? sessionId) => Request(new(sessionId?.Trim() ?? string.Empty, null));
        public void CancelRuntime(string? sessionId, RuntimeIdentity runtime)
            => Request(new(sessionId?.Trim() ?? string.Empty, runtime.Normalized()));
        public void CancelAll() => Request(new(null, null));

        public Drain BeginSessionDrain(string? sessionId) => BeginDrain(new(sessionId?.Trim() ?? string.Empty, null));
        public Drain BeginRuntimeDrain(string? sessionId, RuntimeIdentity runtime)
            => BeginDrain(new(sessionId?.Trim() ?? string.Empty, runtime.Normalized()));

        private Lease[] Retire(Scope scope)
        {
            // Caller holds _gate. Already retired leases remain owned and must
            // also be signalled and joined by a later Stop or host shutdown.
            _owned.RemoveWhere(lease => lease.IsCompleted);
            foreach (var key in _entries.Keys.Where(scope.Matches).ToArray()) _entries.Remove(key);
            return _owned.Where(lease => scope.Matches(lease.Key)).ToArray();
        }

        private void Request(Scope scope)
        {
            Lease[] leases;
            lock (_gate) leases = Retire(scope);
            foreach (var lease in leases) lease.RequestCancellation();
        }

        private Drain BeginDrain(Scope scope)
        {
            Drain drain;
            Lease[] leases;
            lock (_gate)
            {
                drain = new(this, scope);
                _blocks.Add(drain);
                leases = Retire(scope);
            }
            // Each lease keeps the original CancelAsync task. A failed or held
            // callback cannot prevent cancellation of the remaining requests.
            foreach (var lease in leases) lease.RequestCancellation();
            drain.Start(leases);
            return drain;
        }

        private async Task JoinAsync(Lease[] leases)
        {
            await Task.WhenAll(leases.Select(lease => lease.JoinAsync())).ConfigureAwait(false);
            lock (_gate)
            {
                _owned.RemoveWhere(lease => lease.IsCompleted);
                if (_failed) throw new InvalidOperationException("Transient request cleanup failed.");
            }
        }

        private void Release(Lease lease)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(lease.Key, out var leases) && leases.Remove(lease) && leases.Count == 0)
                    _entries.Remove(lease.Key);
            }
        }

        private void NoteFailure(Exception error)
        {
            lock (_gate) _failed = true;
            RunnerLog.Warning(nameof(TransientCancellationRegistry), $"Request cleanup failed; errorClass={error.GetType().Name}.");
        }

        public ValueTask DisposeAsync()
        {
            TaskCompletionSource? start = null;
            Task close;
            lock (_gate)
            {
                if (_close is null)
                {
                    _closed = true;
                    start = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    _close = CloseAsync(start.Task);
                }
                close = _close;
            }
            start?.SetResult();
            return new(close);
        }

        private async Task CloseAsync(Task start)
        {
            await start.ConfigureAwait(false);
            using var drain = BeginDrain(new(null, null));
            await drain.Completion.ConfigureAwait(false);
        }
    }
}

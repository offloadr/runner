namespace Offloadr.Runner.Linux;

/// <summary>Upload-specific allowances; established editor streams use their own policy.</summary>
internal sealed record ArtifactUploadPolicy
{
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan ReceiptTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public long MinimumBytesPerSecond { get; init; } = 256 * 1024;
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    public TimeSpan MaximumElapsed(long wireBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wireBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MinimumBytesPerSecond);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MetadataTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(NoProgressTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ReceiptTimeout, TimeSpan.Zero);
        return MetadataTimeout + TimeSpan.FromSeconds(Math.Ceiling((double)wireBytes / MinimumBytesPerSecond)) + ReceiptTimeout;
    }
}

/// <summary>
/// One pending entry's elapsed allowance. Starts at its first dequeue, survives every
/// requeue and I/O attempt, and keeps no timer while the entry waits behind another file.
/// </summary>
internal sealed class ArtifactUploadBudget(ArtifactUploadPolicy policy)
{
    private TimeSpan _maximum = policy.MaximumElapsed(0);
    private long? _started;
    private bool _snapshotBound;

    public bool IsExpired => _started is { } start && policy.Clock.GetElapsedTime(start) >= _maximum;

    public ArtifactUploadAttempt BeginAttempt(CancellationToken shutdown)
    {
        shutdown.ThrowIfCancellationRequested();
        _started ??= policy.Clock.GetTimestamp();
        var remaining = _maximum - policy.Clock.GetElapsedTime(_started.Value);
        if (remaining <= TimeSpan.Zero)
            throw new TimeoutException("Artifact upload exhausted its elapsed allowance across retries.");
        return new ArtifactUploadAttempt(this, policy, remaining, shutdown);
    }

    internal TimeSpan? BindSnapshot(long wireBytes)
    {
        if (_snapshotBound) return null;
        var started = _started ?? throw new InvalidOperationException("An upload must be dequeued before binding its snapshot.");
        // Created can arrive before the editor writes its file. Size only the first
        // opened snapshot, keeping the original dequeue clock and all later retries.
        _maximum = policy.MaximumElapsed(wireBytes);
        _snapshotBound = true;
        return _maximum - policy.Clock.GetElapsedTime(started);
    }
}

/// <summary>Expiry cancels the actual RPC, file reads and sealing work.</summary>
internal sealed class ArtifactUploadAttempt : IDisposable
{
    private readonly ArtifactUploadPolicy _policy;
    private readonly ArtifactUploadBudget _lifetime;
    private readonly CancellationTokenSource _elapsed;
    private readonly CancellationTokenSource _phase;
    private readonly CancellationTokenSource _linked;
    private string _phaseName = "metadata";

    public ArtifactUploadAttempt(ArtifactUploadBudget lifetime, ArtifactUploadPolicy policy, TimeSpan remaining, CancellationToken shutdown)
    {
        _lifetime = lifetime;
        _policy = policy;
        _elapsed = new CancellationTokenSource(remaining, policy.Clock);
        _phase = new CancellationTokenSource(policy.MetadataTimeout, policy.Clock);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _elapsed.Token, _phase.Token);
    }

    public CancellationToken Token => _linked.Token;
    public bool IsTimedOut => _elapsed.IsCancellationRequested || _phase.IsCancellationRequested;
    public string ExpiredAllowance => _elapsed.IsCancellationRequested ? "total elapsed" : _phaseName;

    public void BeginMetadata() => SetPhase("metadata", _policy.MetadataTimeout);
    public void BeginBody() => SetPhase("transfer progress", _policy.NoProgressTimeout);
    public void BeginReceipt() => SetPhase("completion/receipt", _policy.ReceiptTimeout);

    public void BindSnapshot(long wireBytes)
    {
        Token.ThrowIfCancellationRequested();
        if (_lifetime.BindSnapshot(wireBytes) is { } remaining)
        {
            if (remaining <= TimeSpan.Zero)
                throw new TimeoutException("Artifact upload exhausted its elapsed allowance before transfer.");
            _elapsed.CancelAfter(remaining);
        }
    }

    public void BodyWritten(int bytes)
    {
        if (bytes > 0)
            SetPhase("transfer progress", _policy.NoProgressTimeout);
    }

    private void SetPhase(string name, TimeSpan timeout)
    {
        Token.ThrowIfCancellationRequested();
        _phaseName = name;
        _phase.CancelAfter(timeout);
    }

    public void Dispose()
    {
        _linked.Dispose();
        _phase.Dispose();
        _elapsed.Dispose();
    }
}

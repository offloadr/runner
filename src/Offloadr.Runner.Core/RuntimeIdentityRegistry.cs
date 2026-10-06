using System.Collections.Concurrent;

namespace Offloadr.Runner.Core;

public readonly record struct RuntimeIdentity(ulong LifecycleGeneration, ulong RuntimeEpoch, string RuntimeInstanceId)
{
    public bool IsValid => LifecycleGeneration > 0 && RuntimeEpoch > 0 && !string.IsNullOrWhiteSpace(RuntimeInstanceId);
}

public sealed class RuntimeIdentityRegistry
{
    private readonly ConcurrentDictionary<string, RuntimeIdentity> _identities = new(StringComparer.Ordinal);

    public void Set(string sessionId, ulong lifecycleGeneration, ulong runtimeEpoch, string? runtimeInstanceId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var identity = new RuntimeIdentity(lifecycleGeneration, runtimeEpoch, runtimeInstanceId?.Trim() ?? string.Empty);
        if (identity.IsValid)
        {
            _identities[sessionId.Trim()] = identity;
        }
    }

    public bool TryGet(string? sessionId, out RuntimeIdentity identity)
    {
        identity = default;
        return !string.IsNullOrWhiteSpace(sessionId) &&
            _identities.TryGetValue(sessionId.Trim(), out identity);
    }

    public void Remove(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _identities.TryRemove(sessionId.Trim(), out _);
        }
    }

    public bool RemoveIfMatches(
        string? sessionId,
        ulong lifecycleGeneration,
        ulong runtimeEpoch,
        string? runtimeInstanceId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var expected = new RuntimeIdentity(
            lifecycleGeneration,
            runtimeEpoch,
            runtimeInstanceId?.Trim() ?? string.Empty);
        return expected.IsValid && _identities.TryRemove(
            new KeyValuePair<string, RuntimeIdentity>(sessionId.Trim(), expected));
    }
}

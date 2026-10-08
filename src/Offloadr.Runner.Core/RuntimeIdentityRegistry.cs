using System.Collections.Concurrent;

namespace Offloadr.Runner.Core;

public readonly record struct RuntimeIdentity(ulong LifecycleGeneration, ulong RuntimeEpoch, string RuntimeInstanceId)
{
    public bool IsValid => LifecycleGeneration > 0 && RuntimeEpoch > 0 && !string.IsNullOrWhiteSpace(RuntimeInstanceId);

    /// <summary>
    /// The same identity with a GUID instance id in one canonical spelling, so that
    /// identities captured from different messages compare equal.
    /// </summary>
    public RuntimeIdentity Normalized()
        => this with { RuntimeInstanceId = NormalizeInstanceId(RuntimeInstanceId) };

    /// <summary>True when both identities name the same runtime, however the instance id is spelled.</summary>
    public bool SameRuntime(RuntimeIdentity other)
        => Normalized() == other.Normalized();

    public static string NormalizeInstanceId(string? runtimeInstanceId)
        => Guid.TryParse(runtimeInstanceId, out var instance)
            ? instance.ToString("n")
            : runtimeInstanceId?.Trim() ?? string.Empty;
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

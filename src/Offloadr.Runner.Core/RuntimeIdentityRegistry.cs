using System.Collections.Concurrent;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Core;

/// <summary>
/// One runtime of a session, as the control plane names it (<see cref="RuntimeRef"/>). Two
/// identities are the same runtime when their runtime ids are, however the id is spelled; the
/// order only says which of two runtimes is newer.
/// </summary>
public readonly record struct RuntimeIdentity(string RuntimeId, ulong RuntimeOrder)
{
    public bool IsValid => RuntimeOrder is > 0 and <= long.MaxValue && !string.IsNullOrWhiteSpace(RuntimeId);

    /// <summary>
    /// The same identity with a GUID runtime id in one canonical spelling, so that
    /// identities captured from different messages print alike.
    /// </summary>
    public RuntimeIdentity Normalized()
        => this with { RuntimeId = NormalizeRuntimeId(RuntimeId) };

    /// <summary>True when both identities name the same runtime, however the runtime id is spelled.</summary>
    public bool SameRuntime(RuntimeIdentity other)
        => string.Equals(NormalizeRuntimeId(RuntimeId), NormalizeRuntimeId(other.RuntimeId), StringComparison.Ordinal);

    public bool Equals(RuntimeIdentity other) => SameRuntime(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(NormalizeRuntimeId(RuntimeId));

    /// <summary>The identity a message names; an unset reference is the default, invalid identity.</summary>
    public static RuntimeIdentity From(RuntimeRef? runtime)
        => runtime is null ? default : new RuntimeIdentity(runtime.RuntimeId?.Trim() ?? string.Empty, runtime.RuntimeOrder);

    /// <summary>The wire form of this identity, as it was captured.</summary>
    public RuntimeRef ToRef() => new() { RuntimeId = RuntimeId ?? string.Empty, RuntimeOrder = RuntimeOrder };

    public static string NormalizeRuntimeId(string? runtimeId)
        => Guid.TryParse(runtimeId, out var id)
            ? id.ToString("n")
            : runtimeId?.Trim() ?? string.Empty;
}

/// <summary>
/// The runtime each session runs now, and the session's lifecycle generation from the start
/// that launched it. The generation is not a runtime identity: only GPU power commands use it.
/// </summary>
public sealed class RuntimeIdentityRegistry
{
    private sealed record Entry(RuntimeIdentity Runtime, ulong LifecycleGeneration);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Set(string sessionId, RuntimeIdentity runtime, ulong lifecycleGeneration = 0)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        runtime = runtime with { RuntimeId = runtime.RuntimeId?.Trim() ?? string.Empty };
        if (runtime.IsValid)
        {
            _entries[sessionId.Trim()] = new Entry(runtime, lifecycleGeneration);
        }
    }

    public bool TryGet(string? sessionId, out RuntimeIdentity identity)
    {
        if (!string.IsNullOrWhiteSpace(sessionId) && _entries.TryGetValue(sessionId.Trim(), out var entry))
        {
            identity = entry.Runtime;
            return true;
        }

        identity = default;
        return false;
    }

    /// <summary>The lifecycle generation the session's current runtime started with; zero when it has none.</summary>
    public ulong LifecycleGeneration(string? sessionId)
        => !string.IsNullOrWhiteSpace(sessionId) && _entries.TryGetValue(sessionId.Trim(), out var entry)
            ? entry.LifecycleGeneration
            : 0;

    public void Remove(string? sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _entries.TryRemove(sessionId.Trim(), out _);
        }
    }

    public bool RemoveIfMatches(string? sessionId, RuntimeIdentity expected)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || !expected.IsValid)
        {
            return false;
        }

        // Runtime ids can be spelled differently (hyphenated or compact GUIDs), so match
        // on the runtime, then remove exactly the stored entry.
        var key = sessionId.Trim();
        while (_entries.TryGetValue(key, out var current))
        {
            if (!current.Runtime.SameRuntime(expected))
            {
                return false;
            }

            if (_entries.TryRemove(new KeyValuePair<string, Entry>(key, current)))
            {
                return true;
            }
        }

        return false;
    }
}

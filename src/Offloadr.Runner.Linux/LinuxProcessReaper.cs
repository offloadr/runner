using System.Globalization;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Kills every process that runs under a given uid. Session code can double-fork, call
/// setsid or otherwise leave the editor's process tree, so killing that tree is not enough
/// before the session user is removed and its uid becomes reusable.
/// </summary>
public static class LinuxProcessReaper
{
    private const int SigKill = 9;
    private const int SigStop = 19;
    private const long PidfdOpenSyscall = 434;
    private const long PidfdSendSignalSyscall = 424;
    private const int Enosys = 38;
    private const int MaxStopRounds = 50;
    private static readonly TimeSpan RoundDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private static int _pidfdUnavailable;

    /// <summary>
    /// Stops, then kills, every live process owned by <paramref name="userId"/>. Idempotent;
    /// a uid with no processes is a no-op.
    /// </summary>
    /// <returns>True when no live process of the uid remains.</returns>
    public static async Task<bool> KillAllOwnedByAsync(
        uint userId,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (userId == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(userId), "Refusing to kill processes owned by root.");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return true;
        }

        // Freeze first so nothing can fork new processes while the set is being collected.
        for (var round = 0; round < MaxStopRounds; round++)
        {
            var running = 0;
            foreach (var process in EnumerateOwnedLiveProcesses(userId))
            {
                if (!process.Stopped && TrySignal(process.ProcessId, userId, SigStop))
                {
                    running++;
                }
            }

            if (running == 0)
            {
                break;
            }

            await Task.Delay(RoundDelay, cancellationToken).ConfigureAwait(false);
        }

        var deadline = DateTime.UtcNow + (timeout ?? DefaultTimeout);
        while (true)
        {
            var alive = 0;
            foreach (var process in EnumerateOwnedLiveProcesses(userId))
            {
                alive++;
                TrySignal(process.ProcessId, userId, SigKill);
            }

            if (alive == 0)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(RoundDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Process ids of live (not zombie) processes whose real, effective, saved or filesystem uid matches.</summary>
    public static IReadOnlyList<int> FindOwnedLiveProcesses(uint userId)
        => EnumerateOwnedLiveProcesses(userId).Select(static process => process.ProcessId).ToArray();

    private static IEnumerable<OwnedProcess> EnumerateOwnedLiveProcesses(uint userId)
    {
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateDirectories("/proc").ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        var self = Environment.ProcessId;
        foreach (var entry in entries)
        {
            if (!int.TryParse(Path.GetFileName(entry), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) ||
                pid <= 1 ||
                pid == self)
            {
                continue;
            }

            if (TryReadStatus(pid, out var state, out var owned, userId) && owned && state is not ('Z' or 'X'))
            {
                yield return new OwnedProcess(pid, state is 'T' or 't');
            }
        }
    }

    private static bool TryReadStatus(int pid, out char state, out bool owned, uint userId)
    {
        state = '\0';
        owned = false;
        string[] lines;
        try
        {
            lines = File.ReadAllLines($"/proc/{pid.ToString(CultureInfo.InvariantCulture)}/status");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var sawUid = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("State:", StringComparison.Ordinal))
            {
                var value = line.AsSpan("State:".Length).Trim();
                state = value.IsEmpty ? '\0' : value[0];
            }
            else if (line.StartsWith("Uid:", StringComparison.Ordinal))
            {
                sawUid = true;
                foreach (var field in line["Uid:".Length..].Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries))
                {
                    if (uint.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out var uid) && uid == userId)
                    {
                        owned = true;
                    }
                }
            }
        }

        return sawUid && state != '\0';
    }

    /// <summary>
    /// Signals <paramref name="pid"/> only if it is still owned by <paramref name="userId"/>.
    /// A pidfd pins the process, so a recycled pid is never signalled.
    /// </summary>
    private static bool TrySignal(int pid, uint userId, int signal)
    {
        if (Volatile.Read(ref _pidfdUnavailable) == 0)
        {
            int pidfd;
            try
            {
                pidfd = (int)syscall(PidfdOpenSyscall, pid, 0);
            }
            catch (EntryPointNotFoundException)
            {
                pidfd = -1;
                Volatile.Write(ref _pidfdUnavailable, 1);
            }

            if (pidfd >= 0)
            {
                try
                {
                    return TryReadStatus(pid, out var state, out var owned, userId) &&
                           owned &&
                           state is not ('Z' or 'X') &&
                           syscall(PidfdSendSignalSyscall, pidfd, signal, IntPtr.Zero, 0) == 0;
                }
                finally
                {
                    close(pidfd);
                }
            }

            if (Marshal.GetLastPInvokeError() != Enosys)
            {
                // The process is gone (or otherwise not addressable).
                return false;
            }

            Volatile.Write(ref _pidfdUnavailable, 1);
        }

        return TryReadStatus(pid, out var fallbackState, out var fallbackOwned, userId) &&
               fallbackOwned &&
               fallbackState is not ('Z' or 'X') &&
               kill(pid, signal) == 0;
    }

    private readonly record struct OwnedProcess(int ProcessId, bool Stopped);

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int pid, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int pidfd, int signal, IntPtr info, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int signal);

    [DllImport("libc")]
    private static extern int close(int fd);
}

using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>Process-wide settings for processes that hold sealed session data.</summary>
public static class LinuxProcessHardening
{
    private const int PrGetDumpable = 3;
    private const int PrSetDumpable = 4;
    private const int RlimitCore = 4;

    /// <summary>
    /// Makes this process non-dumpable: no core dump (even one piped to a host helper) and no
    /// ptrace by same-uid processes. Its core size limit drops to 1 byte, hard limit included, so
    /// processes started from it after this cannot dump either, even if their code sets itself
    /// dumpable again: 1 byte is below any core file, and the kernel skips a piped core handler
    /// at exactly that limit (and at no other: a pipe ignores the limit otherwise). Only a
    /// process with CAP_SYS_RESOURCE could raise it again, which session users lack. Returns
    /// false where it is not supported, or where the limit cannot stop the host's core handler.
    /// </summary>
    public static bool DisableCoreDumps()
        => OperatingSystem.IsLinux() && prctl(PrSetDumpable, 0, 0, 0, 0) == 0 && LimitCoreSize();

    /// <summary>The core size limits (soft, hard), or null where they cannot be read.</summary>
    internal static (ulong Soft, ulong Hard)? ReadCoreSizeLimit()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var limit = new Rlimit();
        return getrlimit(RlimitCore, ref limit) == 0 ? (limit.Current, limit.Maximum) : null;
    }

    /// <summary>Sets both core size limits; raising them back needs root (tests only).</summary>
    internal static bool SetCoreSizeLimit(ulong soft, ulong hard)
    {
        var limit = new Rlimit { Current = soft, Maximum = hard };
        return OperatingSystem.IsLinux() && setrlimit(RlimitCore, ref limit) == 0;
    }

    private const string CorePatternPath = "/proc/sys/kernel/core_pattern";

    private static bool LimitCoreSize()
    {
        if (ReadCoreSizeLimit() is not { } current)
        {
            return false;
        }

        // Exactly 1 byte, which raising a hard limit of 0 needs CAP_SYS_RESOURCE for: without
        // it, 0 for both still stops a core file.
        if (current != (1, 1) && !SetCoreSizeLimit(1, 1) && current.Hard == 0)
        {
            _ = SetCoreSizeLimit(0, 0);
        }

        return StopsCoreDumps(ReadCoreSizeLimit(), ReadCorePattern());
    }

    /// <summary>
    /// Whether a core size limit stops every core dump under the host's core pattern. A core file
    /// needs more than the limit; a core piped to a helper (<c>|</c>) is skipped only at a limit
    /// of exactly 1; one handed to a socket (<c>@</c>) by no limit at all. An unreadable pattern
    /// stops nothing for sure.
    /// </summary>
    internal static bool StopsCoreDumps((ulong Soft, ulong Hard)? limit, string? corePattern)
        => limit is { } current && corePattern is not null && current.Soft <= current.Hard && corePattern switch
        {
            _ when corePattern.StartsWith('@') => false,
            _ when corePattern.StartsWith('|') => current == (1, 1),
            _ => current.Hard <= 1,
        };

    private static string? ReadCorePattern()
    {
        try
        {
            return File.ReadAllText(CorePatternPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Makes this process non-dumpable, and throws on Linux when the host forbids it (a seccomp
    /// profile can): sealed data must not be held by a process that can dump core.
    /// </summary>
    public static void RequireCoreDumpsDisabled()
    {
        if (OperatingSystem.IsLinux() && !DisableCoreDumps())
        {
            throw new InvalidOperationException("Sealed storage is unavailable: this host does not allow the runner to disable its core dumps.");
        }
    }

    /// <summary>The current dumpable flag, or null where it cannot be read.</summary>
    public static int? ReadDumpable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        var value = prctl(PrGetDumpable, 0, 0, 0, 0);
        return value < 0 ? null : value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rlimit
    {
        public ulong Current;
        public ulong Maximum;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getrlimit(int resource, ref Rlimit limit);

    [DllImport("libc", SetLastError = true)]
    private static extern int setrlimit(int resource, ref Rlimit limit);

    [DllImport("libc", SetLastError = true, EntryPoint = "prctl")]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}

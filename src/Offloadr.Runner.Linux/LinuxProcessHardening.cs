using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>Process-wide settings for processes that hold sealed session data.</summary>
public static class LinuxProcessHardening
{
    private const int PrGetDumpable = 3;
    private const int PrSetDumpable = 4;

    /// <summary>
    /// Makes this process non-dumpable: no core dump (even one piped to a host helper) and no
    /// ptrace by same-uid processes. Returns false where it is not supported.
    /// </summary>
    public static bool DisableCoreDumps()
        => OperatingSystem.IsLinux() && prctl(PrSetDumpable, 0, 0, 0, 0) == 0;

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

    [DllImport("libc", SetLastError = true, EntryPoint = "prctl")]
    private static extern int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
}

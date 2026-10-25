using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>A pinned direct child: signals never resolve a recycled numeric pid.</summary>
internal sealed class SessionProcessSignal : IDisposable
{
    private const int NoSuchProcess = 3;
    private readonly object _gate = new();
    private int _descriptor;

    internal static string? GetUnsupportedReason()
        => GetUnsupportedReason(
            () => (int)Open(434, Environment.ProcessId, 0),
            descriptor => (int)SendSignal(424, descriptor, 0, IntPtr.Zero, 0),
            descriptor => Close(descriptor),
            Marshal.GetLastPInvokeError);

    internal static string? GetUnsupportedReason(
        Func<int> openSelf,
        Func<int, int> checkSignal,
        Action<int> close,
        Func<int> lastError)
    {
        var descriptor = openSelf();
        if (descriptor < 0)
            return $"Session process ownership requires pidfd_open; kernel or container policy rejected it with errno={lastError()}.";
        try
        {
            // Signal zero checks permission without delivering a signal to the agent.
            return checkSignal(descriptor) == 0 ? null
                : $"Session process ownership requires pidfd_send_signal; kernel or container policy rejected it with errno={lastError()}.";
        }
        finally { close(descriptor); }
    }

    public SessionProcessSignal(Process process)
    {
        _descriptor = (int)Open(434, process.Id, 0);
        var error = Marshal.GetLastPInvokeError();
        // Process tracks the original direct child's exit even if its pid was reused before open.
        if (process.HasExited)
        {
            if (_descriptor >= 0) Close(_descriptor);
            _descriptor = -1;
        }
        else if (_descriptor < 0)
            throw new Win32Exception(error, "Cannot pin the session process for termination.");
    }

    public void Send(int signal)
    {
        lock (_gate)
        {
            if (_descriptor < 0) return;
            if (SendSignal(424, _descriptor, signal, IntPtr.Zero, 0) != 0 && Marshal.GetLastPInvokeError() != NoSuchProcess)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Cannot signal the pinned session process.");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_descriptor >= 0) Close(_descriptor);
            _descriptor = -1;
        }
    }

    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long Open(long number, int pid, uint flags);
    [DllImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static extern long SendSignal(long number, int descriptor, int signal, IntPtr info, uint flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}

using System.Runtime.InteropServices;

namespace Offloadr.Runner.Desktop;

/// <summary>
/// Listens on the app's window for sleep and the end of the Windows session. Before sleep and after
/// waking the supervisor is told, so it checks the runner, Docker and the GPU again at once. When Windows
/// shuts down, restarts or signs out, the runner is stopped quickly and its container (which holds the
/// runner key) removed before Windows ends the app; meanwhile Windows names the reason it waits.
/// Windows sends these messages to hidden windows too, so the app hears them from the notification area.
/// </summary>
internal sealed class WindowLifecycleHook : IDisposable
{
    private static readonly UIntPtr SubclassId = 0x0FF2;

    /// <summary>
    /// How long a session end waits for the runner to stop. Windows shows the reason meanwhile, and lets
    /// the person end the app sooner.
    /// </summary>
    private static readonly TimeSpan SessionEndBudget = TimeSpan.FromSeconds(25);

    private readonly IntPtr _window;
    private readonly RunnerSupervisor _supervisor;
    private readonly Func<Task> _stopForSessionEnd;
    private readonly ILogger _logger;
    private readonly SubclassProc _subclass;
    private bool _subclassed;
    private bool _blocking;

    public WindowLifecycleHook(IntPtr window, RunnerSupervisor supervisor, Func<Task> stopForSessionEnd, ILogger logger)
    {
        _window = window;
        _supervisor = supervisor;
        _stopForSessionEnd = stopForSessionEnd;
        _logger = logger;
        _subclass = WindowProc;
        _subclassed = SetWindowSubclass(_window, _subclass, SubclassId, UIntPtr.Zero);
        if (!_subclassed)
        {
            _logger.LogWarning("Could not listen for sleep and Windows shutdown on the app window.");
        }
    }

    public void Dispose()
    {
        if (_subclassed)
        {
            RemoveWindowSubclass(_window, _subclass, SubclassId);
            _subclassed = false;
        }

        Unblock();
    }

    private IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData)
    {
        switch (WindowsSystemEvents.Decode(message, wParam))
        {
            case SystemEvent.Suspending:
                _supervisor.NotifySuspending();
                break;
            case SystemEvent.Resumed:
                _supervisor.NotifyResumed();
                break;
            case SystemEvent.SessionEndQuery:
                // Windows may go ahead; while the runner stops, it shows why the app holds it up.
                if (_supervisor.View.IsActive || _supervisor.View.State == SupervisorState.Stopping)
                {
                    _blocking = ShutdownBlockReasonCreate(hwnd, "Stopping the Offloadr runner");
                }

                break;
            case SystemEvent.SessionEnding:
                StopForSessionEnd();
                break;
            case SystemEvent.SessionEndCancelled:
                Unblock();
                break;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    /// <summary>
    /// Stops everything before returning: once this message is answered, Windows may end the process.
    /// The work runs off this thread, which the stop does not need.
    /// </summary>
    private void StopForSessionEnd()
    {
        _supervisor.UseFastStop();
        try
        {
            if (!Task.Run(_stopForSessionEnd).Wait(SessionEndBudget))
            {
                _logger.LogWarning("The runner did not stop before Windows ended the session.");
            }
        }
        catch (AggregateException ex)
        {
            _logger.LogError(ex.InnerException ?? ex, "Could not stop the runner as Windows ended the session.");
        }
        finally
        {
            Unblock();
        }
    }

    private void Unblock()
    {
        if (_blocking)
        {
            ShutdownBlockReasonDestroy(_window);
            _blocking = false;
        }
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hwnd, string reason);

    [DllImport("user32.dll")]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hwnd);
}

/// <summary>Keeps Windows from sleeping through a power request, which needs no thread of its own.</summary>
internal sealed class WindowsKeepAwake : IKeepAwake, IDisposable
{
    private const uint PowerRequestContextVersion = 0;
    private const uint PowerRequestContextSimpleString = 0x1;
    private const int PowerRequestSystemRequired = 1;

    private readonly IntPtr _request;
    private readonly ILogger _logger;

    public WindowsKeepAwake(ILogger logger)
    {
        _logger = logger;
        var reason = new ReasonContext
        {
            Version = PowerRequestContextVersion,
            Flags = PowerRequestContextSimpleString,
            SimpleReasonString = "An Offloadr session is running on this computer's GPU.",
        };
        _request = PowerCreateRequest(ref reason);
        if (_request == InvalidHandle)
        {
            _logger.LogWarning("Could not create the power request that keeps the computer awake during sessions.");
        }
    }

    private static readonly IntPtr InvalidHandle = new(-1);

    public void Set(bool awake)
    {
        if (_request == InvalidHandle)
        {
            return;
        }

        var done = awake
            ? PowerSetRequest(_request, PowerRequestSystemRequired)
            : PowerClearRequest(_request, PowerRequestSystemRequired);
        if (!done)
        {
            _logger.LogWarning("Could not change whether the computer stays awake (error {Error}).", Marshal.GetLastPInvokeError());
        }
    }

    public void Dispose()
    {
        if (_request != InvalidHandle)
        {
            CloseHandle(_request);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string SimpleReasonString;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr request, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr request, int type);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}

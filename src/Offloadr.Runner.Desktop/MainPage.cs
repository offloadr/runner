using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Offloadr.Runner.Desktop;

public sealed class App(MainPage page, RunnerSupervisor supervisor, SingleInstanceLock instanceLock) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(page) { Title = "Offloadr Runner", Width = 1100, Height = 860, MinimumWidth = 480, MinimumHeight = 560 };
        window.HandlerChanged += (_, _) => PlatformWindow.Attach(window, page, supervisor, instanceLock);
        return window;
    }
}

public sealed class MainPage : ContentPage
{
    private readonly DesktopRuntime _runtime;
    private readonly RunnerSupervisor _supervisor;
    private readonly AppUpdateService _updates;
    private readonly ILogger<MainPage> _logger;
    private readonly SupervisorPaths _paths;
    private readonly BlazorWebView _webView;
    private readonly TaskCompletionSource<bool> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _loaded;
    private bool _closing;

    public MainPage(DesktopRuntime runtime, RunnerSupervisor supervisor, AppUpdateService updates, SupervisorPaths paths, ILogger<MainPage> logger)
    {
        _runtime = runtime;
        _supervisor = supervisor;
        _updates = updates;
        _paths = paths;
        _logger = logger;
        _webView = new BlazorWebView { HostPage = "wwwroot/index.html" };
        _webView.RootComponents.Add(new RootComponent { Selector = "#app", ComponentType = typeof(Offloadr.Runner.UI.Components.Routes) });

        // Links to other sites open in the person's browser, not inside the app.
        _webView.UrlLoading += (_, args) =>
        {
            if (args.Url.Host != "0.0.0.1" && args.Url.Scheme is "http" or "https")
            {
                args.UrlLoadingStrategy = UrlLoadingStrategy.CancelLoad;
                _ = Launcher.Default.OpenAsync(args.Url);
            }
        };
        Content = new Label { Text = "Opening Offloadr Runner…", Margin = 32 };
    }

    public bool CanClose { get; private set; }

    /// <summary>True once the runner supervisor runs; false when the app could not start (another copy is open, say).</summary>
    public Task<bool> Started => _started.Task;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            await _runtime.StartAsync();
            if (UpdateHandoff.TakeResume(_paths))
            {
                // Just updated with the runner wanted: start it unless startup reconnected to it.
                _supervisor.ResumeAfterUpdate();
            }

            Content = _webView;
            _started.TrySetResult(true);
        }
        catch (AlreadyOpenException ex) when (ex.BroughtUp)
        {
            // The copy that is already open shows its window; this one has nothing to do. Quitting
            // waits for the dispatcher: this runs while the window is still being created.
            CanClose = true;
            _started.TrySetResult(false);
            Dispatcher.Dispatch(() => Application.Current?.Quit());
        }
        catch (Exception ex)
        {
            CanClose = true;
            _started.TrySetResult(false);
            Content = new VerticalStackLayout
            {
                Padding = 32,
                Spacing = 12,
                Children =
                {
                    new Label { Text = "Offloadr Runner could not start.", FontSize = 20 },
                    new Label { Text = ex.Message },
                },
            };
        }
    }

    /// <summary>
    /// Closes the app so the installer can apply the downloaded update and start it again. The runner
    /// keeps running meanwhile, session and all; the new version reconnects to it.
    /// </summary>
    public async Task RestartToUpdateAsync()
    {
        if (_closing || CanClose || !_updates.CanRestartNow(_supervisor.View))
        {
            return;
        }

        _closing = true;
        try
        {
            // First, so a failure leaves the app as it was. The installer waits for this process to exit.
            _updates.ApplyAfterExit(restart: true, []);
            Content = new Label { Text = "Restarting to install the update…", Margin = 32 };
            _supervisor.KeepRunnerOnShutdown();

            // The runner may exit, or start waiting to restart, until the supervisor has stopped;
            // the new version then has nothing to reconnect to, so it is told the runner is wanted.
            var wanted = _supervisor.View;
            await _runtime.StopAsync();
            UpdateHandoff.Save(_paths, wanted.IsActive ? wanted : _supervisor.View);
            CanClose = true;
            Application.Current?.CloseWindow(Window);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not hand the update to the installer.");
            await DisplayAlertAsync("Could not install the update", "Offloadr Runner tries again when you quit it.", "OK");
        }
        finally
        {
            _closing = false;
        }
    }

    /// <summary>
    /// A downloaded update installs once the app has quit, so the next start (at sign-in, say) is the
    /// new version. Called after the runner has stopped: the installer waits only a minute for the exit.
    /// </summary>
    private void InstallDownloadedUpdateAfterExit()
    {
        if (_updates.Status.State != AppUpdateState.Ready)
        {
            return;
        }

        try
        {
            _updates.ApplyAfterExit(restart: false, []);
        }
        catch (Exception ex)
        {
            // It is still downloaded; quitting again tries again.
            _logger.LogError(ex, "Could not hand the update to the installer.");
        }
    }

    /// <summary>Quitting stops the runner container; ask first when it is running, and say so when a session would be cut off.</summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (CanClose)
        {
            return true;
        }

        if (_closing)
        {
            return false;
        }

        _closing = true;
        try
        {
            var view = _supervisor.View;
            if (view.IsActive)
            {
                var message = view.HasActiveSession
                    ? "An editor session is using this runner. Quitting stops the runner and interrupts that session."
                    : "Quitting stops the runner. Offloadr can no longer use this GPU until you open the app again. Close the window instead to keep it running in the notification area.";
                if (!await DisplayAlertAsync("Quit Offloadr Runner?", message, "Quit", "Keep running"))
                {
                    return false;
                }
            }

            Content = new Label { Text = "Stopping the runner…", Margin = 32 };
            await _runtime.StopAsync();
            InstallDownloadedUpdateAfterExit();
            CanClose = true;
            return true;
        }
        finally
        {
            _closing = false;
        }
    }
}

using Windows.Storage.Pickers;

namespace Offloadr.Runner.Desktop;

internal static class PlatformWindow
{
    private static bool _attached;

    /// <summary>
    /// Makes the window live in the notification area once the app has started: closing it hides it,
    /// and the tray menu opens, starts or stops the runner, or quits through
    /// <see cref="MainPage.RequestCloseAsync"/>. An app that could not start gets no tray icon and
    /// keeps its window, which shows why.
    /// </summary>
    public static void Attach(Window window, MainPage page, RunnerSupervisor supervisor, SingleInstanceLock instanceLock)
    {
        if (_attached || window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
        {
            return;
        }

        _attached = true;
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(native);
        var balloonShown = false;
        var activated = false;
        TrayIcon? tray = null;
        TrayStateIcons? icons = null;
        native.Activated += (_, _) => activated = true;

        void Open()
        {
            native.AppWindow.Show();
            if (native.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            {
                presenter.Restore();
            }

            native.Activate();
        }

        void Minimize()
        {
            if (native.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Minimize();
            }
        }

        void HideToTray()
        {
            if (tray is not { IsVisible: true })
            {
                // No icon to come back through (the notification area is not there yet): stay on the taskbar.
                Minimize();
                return;
            }

            native.AppWindow.Hide();
            if (!balloonShown)
            {
                balloonShown = true;
                tray?.ShowBalloon(
                    "Offloadr Runner is still open",
                    supervisor.View.IsActive
                        ? "The runner keeps running. Open or quit the app from this icon."
                        : "Open or quit the app from this icon.");
            }
        }

        async void Quit()
        {
            Open();
            if (await page.RequestCloseAsync())
            {
                native.Close();
            }
        }

        IReadOnlyList<TrayMenuItem> Menu()
        {
            var view = supervisor.View;
            var runnerItem = view.IsActive || view.State == SupervisorState.Stopping
                ? new TrayMenuItem(view.HasActiveSession ? "Stop runner and end the session" : "Stop runner", supervisor.RequestStop, view.State != SupervisorState.Stopping)
                : new TrayMenuItem("Start runner", supervisor.RequestStart);
            return
            [
                new TrayMenuItem("Open Offloadr Runner", Open, IsDefault: true),
                TrayMenuItem.Separator,
                new TrayMenuItem(view.StateTitle, Enabled: false),
                runnerItem,
                TrayMenuItem.Separator,
                new TrayMenuItem("Quit", Quit),
            ];
        }

        native.AppWindow.Closing += (_, args) =>
        {
            if (page.CanClose)
            {
                tray?.Dispose();
                icons?.Dispose();
                return;
            }

            args.Cancel = true;
            if (tray is null)
            {
                // Still starting: closing quits, as before the tray existed.
                Quit();
                return;
            }

            HideToTray();
        };

        _ = AddTrayOnceStartedAsync();

        async Task AddTrayOnceStartedAsync()
        {
            // Only a started app gets an icon: one that could not start (another copy is open, say)
            // would show a menu for a runner it does not run, and must keep its window to say why.
            if (!await page.Started)
            {
                return;
            }

            var stateIcons = new TrayStateIcons(Path.Combine(AppContext.BaseDirectory, "appicon.ico"), TrayIcon.SmallIconSize);
            icons = stateIcons;
            tray = new TrayIcon(handle, stateIcons[TrayStateIcons.BadgeFor(supervisor.View.State)], Tooltip(supervisor.View), Open, Menu);
            instanceLock.OpenRequested += () => native.DispatcherQueue.TryEnqueue(Open);

            // Changed is raised on several threads, so the posted update reads the view itself, on the UI
            // thread: whichever post runs last shows the current state. The check here only skips
            // posts for changes (log lines, say) that leave the tooltip, which names the state and so
            // changes whenever the badge would, as it was.
            var postedTooltip = Tooltip(supervisor.View);
            supervisor.Changed += () =>
            {
                var tooltip = Tooltip(supervisor.View);
                if (Interlocked.Exchange(ref postedTooltip, tooltip) != tooltip)
                {
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        var view = supervisor.View;
                        tray.SetTooltip(Tooltip(view));
                        tray.SetIcon(stateIcons[TrayStateIcons.BadgeFor(view.State)]);
                    });
                }
            };

            // Started from a login shortcut: go straight to the notification area.
            if (Environment.GetCommandLineArgs().Contains("--minimized", StringComparer.OrdinalIgnoreCase))
            {
                balloonShown = true;
                if (activated)
                {
                    native.DispatcherQueue.TryEnqueue(HideToTray);
                }
                else
                {
                    void HideOnce(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs e)
                    {
                        native.Activated -= HideOnce;
                        native.DispatcherQueue.TryEnqueue(HideToTray);
                    }

                    native.Activated += HideOnce;
                }
            }
        }
    }

    private static string Tooltip(SupervisorView view) => $"Offloadr Runner: {view.StateTitle}";
}

internal static class PlatformFiles
{
    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var native = (Microsoft.UI.Xaml.Window)Application.Current!.Windows[0].Handler!.PlatformView!;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(native));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
}

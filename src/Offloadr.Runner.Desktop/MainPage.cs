using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Maui;

namespace Offloadr.Runner.Desktop;

public sealed class App(MainPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(page) { Title = "Offloadr Runner", Width = 1100, Height = 860, MinimumWidth = 480, MinimumHeight = 560 };
        window.HandlerChanged += (_, _) => PlatformWindow.Attach(window, page);
        return window;
    }
}

public sealed class MainPage : ContentPage
{
    private readonly DesktopRuntime _runtime;
    private readonly RunnerSupervisor _supervisor;
    private readonly BlazorWebView _webView;
    private bool _loaded;
    private bool _closing;

    public MainPage(DesktopRuntime runtime, RunnerSupervisor supervisor)
    {
        _runtime = runtime;
        _supervisor = supervisor;
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
            Content = _webView;
        }
        catch (Exception ex)
        {
            CanClose = true;
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
                    : "Quitting stops the runner. Offloadr can no longer use this GPU until you open the app again.";
                if (!await DisplayAlertAsync("Quit Offloadr Runner?", message, "Quit", "Keep running"))
                {
                    return false;
                }
            }

            Content = new Label { Text = "Stopping the runner…", Margin = 32 };
            await _runtime.StopAsync();
            CanClose = true;
            return true;
        }
        finally
        {
            _closing = false;
        }
    }
}

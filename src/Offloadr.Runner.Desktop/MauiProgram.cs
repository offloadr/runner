using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Offloadr.Runner.Desktop;

public static class MauiProgram
{
    /// <summary>Overrides where settings, keys and logs live; handy for trying the app without touching your own settings.</summary>
    public const string DataDirectoryVariable = "OFFLOADR_RUNNER_DATA_DIRECTORY";

    /// <summary>
    /// True when <see cref="DataDirectoryVariable"/> points the app at another data folder (a
    /// development build, say). Such a copy leaves the person's sign-in entry alone.
    /// </summary>
    public static bool UsesOtherDataDirectory => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataDirectoryVariable));

    /// <summary>What "start at sign-in" starts: the installer's launcher when installed, which outlives updates.</summary>
    public static string LaunchPath => Offloadr.Runner.Supervisor.Hosting.WindowsStartupRegistration.LaunchPath(Environment.ProcessPath!);

    public static SupervisorPaths Paths()
    {
        var data = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return string.IsNullOrWhiteSpace(data) ? SupervisorPaths.UserDefault() : new SupervisorPaths(data);
    }

    public static MauiApp CreateMauiApp()
    {
        var paths = Paths();

        // WebView2 keeps its profile next to the executable unless told otherwise, and every update
        // replaces the installed app's folder: keep it with the app's data instead.
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", paths.WebView);
        var builder = MauiApp.CreateBuilder().UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();
        builder.Services.Replace(ServiceDescriptor.Singleton<IHostEnvironment>(new DesktopHostEnvironment()));

        builder.Services.AddDataProtection()
            .SetApplicationName("OffloadrRunner")
            .PersistKeysToFileSystem(new DirectoryInfo(paths.Keys))
            .ProtectKeysWithDpapi();
        builder.Services.AddSingleton<ISecretProtector, DesktopSecretProtector>();
        builder.Services.AddScoped<IHostActions, DesktopHostActions>();

        // Hosted services start in registration order: take the single-instance lock before the supervisor runs.
        builder.Services.AddSingleton(new SingleInstanceLock(paths));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SingleInstanceLock>());
        builder.Services.AddRunnerSupervisor(paths).AddRunnerUI();
        builder.Services.AddRunnerUpdates<Offloadr.Runner.Desktop.WinUI.VelopackUpdateSource>(AppUpdateService.VersionOf(typeof(MauiProgram).Assembly));

        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new Offloadr.Runner.Supervisor.Logging.DesktopDiagnosticLogProvider(paths.Logs));
        builder.Services.AddSingleton<DesktopRuntime>();
        builder.Services.AddSingleton<MainPage>();
#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}

internal sealed class DesktopSecretProtector(IDataProtectionProvider provider) : ISecretProtector
{
    private readonly IDataProtector _protector = provider.CreateProtector("OffloadrRunner.RunnerKey.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string protectedValue) => _protector.Unprotect(protectedValue);
}

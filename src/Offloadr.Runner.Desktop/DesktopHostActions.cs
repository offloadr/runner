using Offloadr.Runner.Supervisor.Hosting;

namespace Offloadr.Runner.Desktop;

public sealed class DesktopHostActions : IHostActions
{
    private readonly WindowsStartupRegistration _startup = new(Environment.ProcessPath!);

    public bool CanPickFolder => true;

    // A copy pointed at another data folder (a development build, say) must not take over the
    // person's sign-in entry, which Windows would start without that folder. Nor is the switch
    // offered when the Run key cannot even be read (a policy, say).
    public bool CanStartAtSignIn => !MauiProgram.UsesOtherDataDirectory && TryReadStartsAtSignIn(out _);

    public bool StartsAtSignIn => TryReadStartsAtSignIn(out var enabled) && enabled;

    public void SetStartAtSignIn(bool enabled) => _startup.SetEnabled(enabled);

    private bool TryReadStartsAtSignIn(out bool enabled)
    {
        try
        {
            enabled = _startup.IsEnabled;
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            enabled = false;
            return false;
        }
    }

    public Task<string?> PickFolderAsync() => MainThread.InvokeOnMainThreadAsync(PlatformFiles.PickFolderAsync);

    public async Task OpenExternalAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            await Launcher.Default.OpenAsync(uri);
        }
    }
}

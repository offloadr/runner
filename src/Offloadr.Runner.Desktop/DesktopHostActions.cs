namespace Offloadr.Runner.Desktop;

public sealed class DesktopHostActions : IHostActions
{
    public bool CanPickFolder => true;

    public Task<string?> PickFolderAsync() => MainThread.InvokeOnMainThreadAsync(PlatformFiles.PickFolderAsync);

    public async Task OpenExternalAsync(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            await Launcher.Default.OpenAsync(uri);
        }
    }
}

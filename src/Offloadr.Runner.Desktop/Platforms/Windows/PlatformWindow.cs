using Windows.Storage.Pickers;

namespace Offloadr.Runner.Desktop;

internal static class PlatformWindow
{
    /// <summary>Routes the window's close button through <see cref="MainPage.RequestCloseAsync"/>.</summary>
    public static void Attach(Window window, MainPage page)
    {
        if (window.Handler?.PlatformView is not Microsoft.UI.Xaml.Window native)
        {
            return;
        }

        native.AppWindow.Closing += async (_, args) =>
        {
            if (page.CanClose)
            {
                return;
            }

            args.Cancel = true;
            if (await page.RequestCloseAsync())
            {
                native.Close();
            }
        };
    }
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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.JSInterop;
using MudBlazor;
using MudBlazor.Services;

namespace Offloadr.Runner.UI;

/// <summary>What the UI needs from the app that hosts it.</summary>
public interface IHostActions
{
    /// <summary>Whether the host has a native folder chooser.</summary>
    bool CanPickFolder { get; }

    Task<string?> PickFolderAsync();

    /// <summary>Opens an http(s) link in the person's browser.</summary>
    Task OpenExternalAsync(string url);

    /// <summary>Whether the host can start itself when the person signs in.</summary>
    bool CanStartAtSignIn => false;

    bool StartsAtSignIn => false;

    /// <summary>Turns starting at sign-in on or off; throws when the host cannot change it.</summary>
    void SetStartAtSignIn(bool enabled) => throw new NotSupportedException("This host cannot start at sign-in.");
}

/// <summary>Fallback for hosts that render in a browser: no folder picker, links open in a new tab.</summary>
public sealed class BrowserHostActions(IJSRuntime js) : IHostActions
{
    public bool CanPickFolder => false;

    public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);

    public async Task OpenExternalAsync(string url) => await js.InvokeVoidAsync("open", url, "_blank", "noopener");
}

public static class RunnerUiServices
{
    public static IServiceCollection AddRunnerUI(this IServiceCollection services)
    {
        services.AddMudServices(config => config.SnackbarConfiguration.PositionClass = Defaults.Classes.Position.BottomRight);
        services.TryAddScoped<IHostActions, BrowserHostActions>();
        return services;
    }
}

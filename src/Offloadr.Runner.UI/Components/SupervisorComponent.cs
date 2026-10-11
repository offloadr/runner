using Microsoft.AspNetCore.Components;
using Offloadr.Runner.Supervisor;

namespace Offloadr.Runner.UI.Components;

/// <summary>
/// Base for pages that show supervisor state. Changes can arrive hundreds of times a second while an
/// image downloads, so the component re-renders at most four times a second.
/// </summary>
public abstract class SupervisorComponent : ComponentBase, IDisposable
{
    private static readonly TimeSpan RenderInterval = TimeSpan.FromMilliseconds(250);

    private readonly CancellationTokenSource _disposed = new();
    private int _dirty;

    [Inject]
    protected RunnerSupervisor Supervisor { get; set; } = null!;

    protected SupervisorView View => Supervisor.View;

    public void Dispose()
    {
        Supervisor.Changed -= OnChanged;
        _disposed.Cancel();
        _disposed.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void OnInitialized()
    {
        Supervisor.Changed += OnChanged;
        _ = RenderLoopAsync(_disposed.Token);
    }

    private void OnChanged() => Interlocked.Exchange(ref _dirty, 1);

    private async Task RenderLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RenderInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (Interlocked.Exchange(ref _dirty, 0) == 1)
                {
                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The page closed.
        }
    }
}

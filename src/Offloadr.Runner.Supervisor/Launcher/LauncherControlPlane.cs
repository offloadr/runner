using System.Runtime.CompilerServices;
using Grpc.Net.Client;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.Launcher;

/// <summary>The four calls a launcher makes. A seam so the link can be tested without a server.</summary>
internal interface ILauncherControlPlane
{
    Task<RegisterRunnerResponse> RegisterAsync(RunnerSettings settings, RegisterRunnerRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<LauncherStreamResponse> StreamAsync(RunnerSettings settings, LauncherStreamRequest request, CancellationToken cancellationToken);

    Task ReportAsync(RunnerSettings settings, ReportRunnerLaunchRequest request, CancellationToken cancellationToken);

    Task<ReleaseRunnerResponse> ReleaseAsync(RunnerSettings settings, ReleaseRunnerRequest request, CancellationToken cancellationToken);
}

/// <summary>The launcher's calls over gRPC, authenticated with the runner key, one channel per address.</summary>
internal sealed class GrpcLauncherControlPlane : ILauncherControlPlane, IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    // Short, so a report that is lost leaves time for its retry within the protocol's 30 seconds
    // (with the link's 15-second repeat and 1-second retry backoff: 15 + 5 + 1 + 5 = 26).
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public async Task<RegisterRunnerResponse> RegisterAsync(RunnerSettings settings, RegisterRunnerRequest request, CancellationToken cancellationToken)
        => await Client(settings).RegisterRunnerAsync(request, deadline: DateTime.UtcNow + CallTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<LauncherStreamResponse> StreamAsync(
        RunnerSettings settings,
        LauncherStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = Client(settings).LauncherStream(request, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task ReportAsync(RunnerSettings settings, ReportRunnerLaunchRequest request, CancellationToken cancellationToken)
        => await Client(settings).ReportRunnerLaunchAsync(request, deadline: DateTime.UtcNow + ReportTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<ReleaseRunnerResponse> ReleaseAsync(RunnerSettings settings, ReleaseRunnerRequest request, CancellationToken cancellationToken)
        => await Client(settings).ReleaseRunnerAsync(request, deadline: DateTime.UtcNow + CallTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        lock (_channels)
        {
            _disposed = true;
            foreach (var channel in _channels.Values)
            {
                channel.Dispose();
            }

            _channels.Clear();
        }
    }

    private RunnerControlService.RunnerControlServiceClient Client(RunnerSettings settings)
    {
        // The link sends nothing unless the address is https and a key is set.
        GrpcChannel channel;
        lock (_channels)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_channels.TryGetValue(settings.OffloadrApiOrigin, out channel!))
            {
                channel = GrpcChannelManager.CreateChannel(settings.OffloadrApiOrigin, traceHttp: false);
                _channels[settings.OffloadrApiOrigin] = channel;
            }
        }

        return new RunnerControlService.RunnerControlServiceClient(
            GrpcChannelManager.CreateAuthenticatedInvoker(channel.CreateCallInvoker(), settings.RunnerKey!));
    }
}

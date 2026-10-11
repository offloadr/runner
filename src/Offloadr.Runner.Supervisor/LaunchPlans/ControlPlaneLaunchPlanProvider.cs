using System.Collections.Concurrent;
using System.Reflection;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Offloadr.Runner.Core;
using Offloadr.Runner.Supervisor.Settings;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Supervisor.LaunchPlans;

/// <summary>The control plane rejected the runner key.</summary>
public sealed class LaunchPlanRejectedException(string message) : Exception(message);

/// <summary>
/// Asks the control plane which image to run (<c>GetRunnerLaunchPlan</c>), authenticated with the
/// runner key. Falls back to <see cref="BuiltInLaunchPlanCatalog"/> when the server does not offer
/// the call yet or cannot be reached, so the runner can still start; the container retries on its own.
/// </summary>
public sealed class ControlPlaneLaunchPlanProvider : ILaunchPlanProvider, IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);
    private static readonly string SupervisorVersion = typeof(ControlPlaneLaunchPlanProvider).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<RunnerSettings, CancellationToken, Task<GetRunnerLaunchPlanResponse>> _call;

    public ControlPlaneLaunchPlanProvider()
    {
        _call = CallAsync;
    }

    internal ControlPlaneLaunchPlanProvider(Func<RunnerSettings, CancellationToken, Task<GetRunnerLaunchPlanResponse>> call)
    {
        _call = call;
    }

    public async Task<LaunchPlan> GetAsync(RunnerSettings settings, CancellationToken cancellationToken)
    {
        // The key travels as call metadata, so it is never sent to an address that is not https
        // (Settings report that address as invalid; the supervisor may ask before validating).
        if (!settings.HasRunnerKey || !settings.HasHttpsOrigin)
        {
            return BuiltInLaunchPlanCatalog.Plan();
        }

        GetRunnerLaunchPlanResponse response;
        try
        {
            response = await _call(settings, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // Grpc.Net.Client reports a cancelled call as an RpcException; callers tell Stop and
            // shutdown apart from failures by OperationCanceledException.
            throw new OperationCanceledException("The launch plan request was cancelled.", ex, cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented or StatusCode.NotFound)
        {
            return BuiltInLaunchPlanCatalog.Plan("This Offloadr server does not suggest a runner image yet, so the editor chosen in Settings is used.");
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            throw new LaunchPlanRejectedException("Offloadr did not accept the runner key. Check it in Settings, or create a new one in the Offloadr web app.");
        }
        catch (RpcException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return BuiltInLaunchPlanCatalog.Plan(
                $"Could not reach Offloadr ({ex.StatusCode}), so the editor chosen in Settings is used.",
                LaunchPlanSource.Unreachable);
        }
        catch (HttpRequestException ex)
        {
            return BuiltInLaunchPlanCatalog.Plan(
                $"Could not reach Offloadr ({ex.Message}), so the editor chosen in Settings is used.",
                LaunchPlanSource.Unreachable);
        }

        return Map(response);
    }

    internal static LaunchPlan Map(GetRunnerLaunchPlanResponse response)
    {
        var templates = response.Templates
            .Where(template => !string.IsNullOrWhiteSpace(template.EditorTemplate) && !string.IsNullOrWhiteSpace(template.RunnerImage))
            .Select(template => new LaunchTemplate(
                template.EditorTemplate.Trim(),
                template.EditorRuntimeKind.Trim(),
                template.RunnerImage.Trim(),
                string.IsNullOrWhiteSpace(template.Label) ? template.EditorTemplate.Trim() : template.Label.Trim()))
            .ToList();
        if (templates.Count == 0)
        {
            return BuiltInLaunchPlanCatalog.Plan("Offloadr returned no runner images, so the built-in list is used.");
        }

        var refresh = response.RefreshSeconds > 0
            ? TimeSpan.FromSeconds(Math.Clamp(response.RefreshSeconds, 15, 86_400))
            : BuiltInLaunchPlanCatalog.RefreshInterval;
        var selected = string.IsNullOrWhiteSpace(response.SelectedEditorTemplate) ? null : response.SelectedEditorTemplate.Trim();
        return new LaunchPlan(templates, selected, refresh, LaunchPlanSource.ControlPlane);
    }

    public void Dispose()
    {
        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }

        _channels.Clear();
    }

    private async Task<GetRunnerLaunchPlanResponse> CallAsync(RunnerSettings settings, CancellationToken cancellationToken)
    {
        var channel = _channels.GetOrAdd(settings.OffloadrApiOrigin, origin => GrpcChannelManager.CreateChannel(origin, traceHttp: false));
        var client = new RunnerControlService.RunnerControlServiceClient(
            channel.Intercept(new RunnerSecretClientInterceptor(settings.RunnerKey!)));
        return await client.GetRunnerLaunchPlanAsync(
            new GetRunnerLaunchPlanRequest { SupervisorVersion = SupervisorVersion },
            deadline: DateTime.UtcNow + CallTimeout,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

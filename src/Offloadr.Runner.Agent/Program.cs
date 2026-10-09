using Offloadr.Runner.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Runtime.InteropServices;

var options = RunnerAgentOptions.FromEnvironment();
await using var runnerLogRelay = new RunnerLogRelay(options.RunnerId);

using var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.ClearProviders();
    builder.AddSimpleConsole(console =>
    {
        console.SingleLine = true;
        console.TimestampFormat = "HH:mm:ss ";
    });
    builder.AddProvider(new RunnerLogForwardingProvider(runnerLogRelay));
    builder.SetMinimumLevel(LogLevel.Information);
});

RunnerLog.Configure(loggerFactory);
var sessionIsolationStrategy = new LinuxUserIsolationStrategy(new LinuxCommandRunner());
var startupValidator = new RunnerStartupValidator(sessionIsolationStrategy);

try
{
    startupValidator.ValidateOrThrow(options);
}
catch (Exception ex)
{
    // Exit non-zero so supervisors and restart policies see a failed start.
    RunnerLog.Error(ex.Message);
    return 1;
}

RunnerResources resources = ResourceDetection.BuildInitialResources();
var version = typeof(RunnerAgentOptions).Assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
var instanceId = Guid.NewGuid().ToString("n");

RunnerLog.Info(
    $"Runner starting id={options.RunnerId} version={version} offloadr-api={GrpcChannelManager.RedactForLog(options.OffloadrApiUrl)} " +
    $"instance={instanceId} mode=grpc grpc-trace={(options.TraceGrpcHttp ? "on" : "off")}");
RunnerLog.Info("Offloadr runner is free software under AGPL-3.0-only; source: https://github.com/offloadr/runner");

using var shutdown = new CancellationTokenSource();
var runtimeIdentities = new RuntimeIdentityRegistry();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
    RunnerLog.Info("Cancellation requested (Ctrl+C)");
};
// Container stops send SIGTERM to PID 1; treat it as a host shutdown so commands drain
// and sessions are cleaned up before the process exits.
using var sigtermRegistration = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    shutdown.Cancel();
    RunnerLog.Info("Termination requested (SIGTERM)");
});

await using var sessionProcessLogRelay = new SessionProcessLogRelay(options.RunnerId, runtimeIdentities);
await using var sessionRuntimeTelemetryRelay = new SessionRuntimeTelemetryRelay(options.RunnerId, runtimeIdentities);
using var sessionManager = new SessionProcessManager(options.Session, sessionIsolationStrategy, new RunnerVfsEnvironmentBuilder(), new LinuxCommandRunner(), sessionProcessLogRelay);
// Before any session starts: users, processes and homes left by a crashed earlier run.
try
{
    await sessionManager.CleanupStaleSessionsAsync(shutdown.Token);
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    return 0;
}
var logicalSessionState = new ServiceClientManager.LogicalSessionState(sessionManager.GetActiveSessionId());
var hydrationCapabilities = ModelHydrationRuntimeCapabilities.FromEnvironment(
    options.Session.RuntimeKind);
var modelProjectionRoot = string.Equals(options.Session.RuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
    ? options.Session.SessionRoot
    : "/comfyui/models";
string[] modelRoots = [options.Aria2.DownloadDirectory, modelProjectionRoot, .. hydrationCapabilities.PersistentModelRoots];
LinuxAria2FileAccess aria2FileAccess;
try
{
    // aria2 runs unprivileged and writes in place only beneath model roots no session can
    // write; destinations inside session homes are staged and published by the agent.
    aria2FileAccess = LinuxAria2FileAccess.Create(options.Aria2, modelRoots, options.Session.SessionRoot);
}
catch (Exception ex)
{
    RunnerLog.Error(ex.Message);
    return 1;
}

await using IModelTransferBackend downloadBackend = new Aria2DownloadBackend(options.Aria2, fileAccess: aria2FileAccess);
using var artifactGrpcChannel = GrpcChannelManager.CreateChannel(
    options.OffloadrApiUrl,
    options.TraceGrpcHttp);

var artifactCatalogClient = new RunnerArtifactService.RunnerArtifactServiceClient(artifactGrpcChannel);
var workspaceCatalogClient = new RunnerWorkspaceService.RunnerWorkspaceServiceClient(artifactGrpcChannel);
await using var artifactUploadService = new ArtifactUploadService(
    options.RunnerSecret,
    artifactCatalogClient,
    loggerFactory.CreateLogger<ArtifactUploadService>(),
    runtimeIdentities,
    options.RunnerId);
await using var workspaceMirrorService = new WorkspaceMirrorService(
    workspaceCatalogClient,
    options.RunnerSecret,
    loggerFactory.CreateLogger<WorkspaceMirrorService>());
await using var downloadService = new ModelDownloadService(
    downloadBackend,
    options.Aria2,
    hydrationCapabilities,
    new ModelDestinationPolicy(modelRoots),
    new SessionModelDownloadStaging(options.Session.SessionRoot, options.Aria2.StagingDirectory));
await using var sessionEventRelay = new ComfySessionEventRelay(
    options.RunnerId,
    sessionManager.ComfyHost,
    sessionManager.ComfyPort,
    artifactUploadService.CaptureSessionRefresh,
    isTrackedRuntime: sessionManager.IsTrackedRuntime);
var runtimeTelemetryService = new RuntimeTelemetryService(options.Aria2.DownloadDirectory);
var gpuPowerLimitService = new GpuPowerCommandExecutor(new GpuPowerLimitService(runtimeTelemetryService));
var runtimeTelemetryReporter = new ActiveSessionRuntimeTelemetryReporter(
    logicalSessionState.GetActiveSessionId,
    runtimeTelemetryService.TryCaptureSnapshot,
    sessionRuntimeTelemetryRelay.Enqueue,
    options.RuntimeTelemetryInterval,
    sessionId => runtimeIdentities.TryGet(sessionId, out var identity) ? identity : null);
sessionManager.UnexpectedSessionExitCleanup = async (sessionId, exitedRuntime, exitCode, token) =>
{
    var exitSummary = exitCode.HasValue ? $" code={exitCode.Value}" : string.Empty;
    RunnerLog.Warning($"Session '{sessionId}' process exited unexpectedly{exitSummary}; stopping session sidecars.");
    await ServiceClientManager.HandleUnexpectedRuntimeExitAsync(
        sessionId,
        exitedRuntime,
        logicalSessionState,
        runtimeIdentities,
        (exitedSessionId, cancellationToken) => ServiceClientManager.CleanupExitedSessionAsync(
            exitedSessionId,
            sessionEventRelay.StopSessionAsync,
            artifactUploadService.StopSessionAsync,
            workspaceMirrorService.StopSessionAsync,
            downloadService.CancelSession,
            downloadService.GetActiveSessionId,
            downloadService.SetActiveSession,
            cancellationToken),
        token).ConfigureAwait(false);
};

await using var downloadIpcServer = new ModelDownloadIpcServer(
    options.ModelFetchSocket,
    downloadService,
    artifactUploadService,
    workspaceMirrorService,
    sessionManager.GetSessionUserId);

try
{
    await downloadBackend.StartAsync(shutdown.Token);
    await downloadService.InitializeAsync(shutdown.Token);
    RunnerLog.Info(
        $"Download backend '{downloadBackend.Name}' ready on port {options.Aria2.RpcPort} " +
        $"(dir='{options.Aria2.DownloadDirectory}')");
    downloadIpcServer.Start();
    RunnerLog.Info($"Model download IPC listening at {options.ModelFetchSocket}");
}
catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
{
    RunnerLog.Info("Startup cancelled before download backend ready.");
    return 0;
}
catch (Exception ex)
{
    RunnerLog.Error(ex, $"Failed to initialize download backend: {ex.Message}");
    return 1;
}

LocalModelScanner? scanner = null;
LocalModelProjector? projector = null;
if (!string.IsNullOrWhiteSpace(options.LocalModelsDirectory))
{
    scanner = new LocalModelScanner(options.RunnerId, options.LocalModelsDirectory);
    projector = new LocalModelProjector(options.LocalModelsDirectory, modelProjectionRoot, options.Session.RuntimeKind);
    RunnerLog.Info(
        $"Local model scanning enabled dir='{options.LocalModelsDirectory}' interval={options.LocalModelsScanInterval.TotalSeconds:F0}s");
}

var commandTask = ServiceClientManager.CommandListenerLoopAsync(
    options.OffloadrApiUrl,
    options.TraceGrpcHttp,
    options.RunnerSecret,
    options.RunnerId,
    instanceId,
    options.RegistrationMinBackoff,
    options.RegistrationMaxBackoff,
    options.RegistrationRpcTimeout,
    version,
    sessionManager,
    downloadService,
    sessionEventRelay,
    sessionProcessLogRelay,
    runnerLogRelay,
    sessionRuntimeTelemetryRelay,
    gpuPowerLimitService,
    artifactUploadService,
    workspaceMirrorService,
    logicalSessionState,
    resources,
    options.SupportedEditorTemplates,
    scanner is null ? null : () => scanner.Scan(),
    projector is null ? null : snapshot => projector.UpdateSnapshot(snapshot),
    projector is null ? null : (downloads, replaceExisting) =>
    {
        if (replaceExisting)
        {
            projector.SetRequestedModels(downloads);
        }
        else
        {
            projector.AddRequestedModels(downloads);
        }
    },
    options.LocalModelsScanInterval,
    runtimeIdentities,
    shutdown.Token);

var runtimeTelemetryTask = runtimeTelemetryReporter.RunAsync(shutdown.Token);

var statusFilePath = RunnerStatusFileWriter.ResolvePath(Environment.GetEnvironmentVariable);
var statusFileTask = statusFilePath is null
    ? Task.CompletedTask
    : new RunnerStatusFileWriter(
        statusFilePath,
        options.RunnerId,
        instanceId,
        version,
        options.SupportedEditorTemplates,
        logicalSessionState.GetActiveSessionId).RunAsync(shutdown.Token);

try
{
    await Task.WhenAll(commandTask, runtimeTelemetryTask, statusFileTask);
}
finally
{
    try
    {
        await sessionManager.StopAllAsync(CancellationToken.None);
    }
    catch (Exception ex)
    {
        RunnerLog.Error(ex, $"Failed to stop active sessions during shutdown: {ex.Message}");
    }
}

RunnerLog.Info("Runner agent exiting.");
return 0;

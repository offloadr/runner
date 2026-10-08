using Offloadr.Runner.V1;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System.Reflection;

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
    $"Runner starting id={options.RunnerId} version={version} offloadr-api={options.OffloadrApiUrl} " +
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

await using var sessionProcessLogRelay = new SessionProcessLogRelay(options.RunnerId, runtimeIdentities);
await using var sessionRuntimeTelemetryRelay = new SessionRuntimeTelemetryRelay(options.RunnerId, runtimeIdentities);
using var sessionManager = new SessionProcessManager(options.Session, sessionIsolationStrategy, new RunnerVfsEnvironmentBuilder(), new LinuxCommandRunner(), sessionProcessLogRelay);
var logicalSessionState = new ServiceClientManager.LogicalSessionState(sessionManager.GetActiveSessionId());
await using IModelTransferBackend downloadBackend = new Aria2DownloadBackend(options.Aria2);
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
var hydrationCapabilities = ModelHydrationRuntimeCapabilities.FromEnvironment(
    options.Session.RuntimeKind);
var modelProjectionRoot = string.Equals(options.Session.RuntimeKind, "forge-neo", StringComparison.OrdinalIgnoreCase)
    ? options.Session.SessionRoot
    : "/comfyui/models";
await using var downloadService = new ModelDownloadService(
    downloadBackend,
    options.Aria2,
    hydrationCapabilities,
    new ModelDestinationPolicy(
        [options.Aria2.DownloadDirectory, modelProjectionRoot, .. hydrationCapabilities.PersistentModelRoots]));
await using var sessionEventRelay = new ComfySessionEventRelay(
    options.RunnerId,
    sessionManager.ComfyHost,
    sessionManager.ComfyPort,
    artifactUploadService.CaptureSessionRefresh);
var runtimeTelemetryService = new RuntimeTelemetryService(options.Aria2.DownloadDirectory);
var gpuPowerLimitService = new GpuPowerCommandExecutor(new GpuPowerLimitService(runtimeTelemetryService));
var runtimeTelemetryReporter = new ActiveSessionRuntimeTelemetryReporter(
    logicalSessionState.GetActiveSessionId,
    runtimeTelemetryService.TryCaptureSnapshot,
    sessionRuntimeTelemetryRelay.Enqueue,
    options.RuntimeTelemetryInterval);
sessionManager.UnexpectedSessionExitCleanup = async (sessionId, exitCode, token) =>
{
    var exitSummary = exitCode.HasValue ? $" code={exitCode.Value}" : string.Empty;
    RunnerLog.Warning($"Session '{sessionId}' process exited unexpectedly{exitSummary}; stopping session sidecars.");
    ServiceClientManager.ClearRuntimeSessionState(logicalSessionState, runtimeIdentities, sessionId);
    await ServiceClientManager.CleanupExitedSessionAsync(
        sessionId,
        sessionEventRelay.StopSessionAsync,
        artifactUploadService.StopSessionAsync,
        workspaceMirrorService.StopSessionAsync,
        downloadService.CancelSession,
        downloadService.GetActiveSessionId,
        downloadService.SetActiveSession,
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

using Offloadr.Runner.V1;
using SealedStorageMode = Offloadr.Runner.Core.SealedStorageMode;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Reflection;
using Offloadr.Runner.Agent;

try
{
    return await RunAgentAsync();
}
catch (Exception error)
{
    // Do not print exception messages, nested exceptions or stacks: a failing
    // producer may carry untrusted diagnostic, prompt or transport content.
    Console.Error.WriteLine($"Runner agent failed; errorClass={error.GetType().Name}.");
    return 1;
}

static async Task<int> RunAgentAsync()
{
    var options = RunnerAgentOptions.FromEnvironment();
    using var statusChannel = GrpcChannelManager.CreateChannel(options.OffloadrApiUrl, options.TraceGrpcHttp);
    using var commandChannel = GrpcChannelManager.CreateChannel(options.OffloadrApiUrl, options.TraceGrpcHttp);
    using var sessionEventChannel = GrpcChannelManager.CreateChannel(options.OffloadrApiUrl, options.TraceGrpcHttp);
    using var opsChannel = GrpcChannelManager.CreateChannel(options.OffloadrApiUrl, options.TraceGrpcHttp);
    var commandClient = GrpcChannelManager.CreateAuthenticatedControlClient(commandChannel, options.RunnerSecret);
    await using var sessionSink = new GrpcRunnerControlSink(
        GrpcChannelManager.CreateAuthenticatedControlClient(statusChannel, options.RunnerSecret),
        commandClient,
        GrpcChannelManager.CreateAuthenticatedControlClient(sessionEventChannel, options.RunnerSecret),
        GrpcChannelManager.CreateAuthenticatedControlClient(opsChannel, options.RunnerSecret),
        options.RegistrationRpcTimeout);
    await using var runnerLogRelay = new RunnerLogRelay(options.RunnerId);

    using var loggerFactory = AgentLogging.CreateFactory(runnerLogRelay);

    RunnerLog.Configure(loggerFactory);
    var sessionIsolationStrategy = new LinuxUserIsolationStrategy(new LinuxCommandRunner());
    var sealedStorageProbe = new SealedStorageProbe();
    var startupValidator = new RunnerStartupValidator(sessionIsolationStrategy, sealedStorageProbe: sealedStorageProbe);

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
    var sealedStorageStatus = sealedStorageProbe.Probe(options.Session.Sealing);
    RunnerLog.Info(
        $"Sealed storage mode={sealedStorageStatus.Mode} root='{sealedStorageStatus.Root}' available={sealedStorageStatus.Available} " +
        $"budget={sealedStorageStatus.BudgetBytes?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
        $"swap-devices={sealedStorageStatus.Swap.DevicesBytes} swap-cgroup={sealedStorageStatus.Swap.CgroupLimit ?? "unknown"} " +
        $"tmpfs-noswap={sealedStorageStatus.Swap.TmpfsNoswap}" +
        (sealedStorageStatus.Reason is null ? string.Empty : $" reason='{sealedStorageStatus.Reason}'"));
    // Auto mode applies it before its first sealed start.
    var sealedHostPreparation = new SealedHostPreparation();
    if (options.Session.Sealing.Mode == SealedStorageMode.Required)
    {
        try
        {
            LinuxProcessHardening.RequireCoreDumpsDisabled();
        }
        catch (InvalidOperationException ex)
        {
            RunnerLog.Error($"RUNNER_SEALED_STORAGE=required: {ex.Message}");
            return 1;
        }
    }

    await using var shutdown = new RunnerAgentLifetime();
    using var shutdownSignals = new RunnerAgentShutdownSignals(shutdown);
    var runtimeIdentities = new RuntimeIdentityRegistry();

    await using var sessionProcessLogRelay = new SessionProcessLogRelay(options.RunnerId, runtimeIdentities);
    await using var sessionRuntimeTelemetryRelay = new SessionRuntimeTelemetryRelay(options.RunnerId, runtimeIdentities);
    using var activityChannel = GrpcChannelManager.CreateChannel(options.OffloadrApiUrl, options.TraceGrpcHttp);
    var activitySink = new GrpcRuntimeActivitySink(
        GrpcChannelManager.CreateAuthenticatedControlClient(activityChannel, options.RunnerSecret), options.RunnerId, instanceId);
    await using var sessionManager = new SessionProcessManager(activitySink, options.Session, sessionIsolationStrategy, new RunnerVfsEnvironmentBuilder(), new LinuxCommandRunner(), sessionProcessLogRelay);
    // Before any session starts: users, processes and homes left by a crashed earlier run.
    try
    {
        await sessionManager.CleanupStaleSessionsAsync(shutdown.Token);
    }
    catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
    {
        return 0;
    }

    // After that cleanup: it finds the homes of removed session users by their unknown owners, which
    // the lockdown hands to root.
    if (options.Session.Sealing.Mode == SealedStorageMode.Required && sealedHostPreparation.EnsureApplied() is { } lockdownFailure)
    {
        RunnerLog.Error($"RUNNER_SEALED_STORAGE=required but {lockdownFailure}");
        return 1;
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
        options.RunnerId,
        instanceId);
    // Sealed data the stale cleanup above removed: what it had not uploaded was never counted.
    foreach (var (staleSessionId, staleGeneration) in sessionManager.TakeStaleSealedSessions())
    {
        artifactUploadService.MarkLossUncounted(staleSessionId, staleGeneration);
    }

    if (sessionManager.SealedLifecyclesUnknown)
    {
        artifactUploadService.MarkEveryLossUncounted();
    }

    await using var workspaceMirrorService = new WorkspaceMirrorService(
        workspaceCatalogClient,
        options.RunnerSecret,
        loggerFactory.CreateLogger<WorkspaceMirrorService>(),
        runtimeIdentities: runtimeIdentities,
        runnerId: options.RunnerId,
        instanceId: instanceId);
    await using var downloadService = new ModelDownloadService(
        downloadBackend,
        options.Aria2,
        hydrationCapabilities,
        new ModelDestinationPolicy(modelRoots),
        new SessionModelDownloadStaging(options.Session.SessionRoot, options.Aria2.StagingDirectory));
    await using var sessionEventRelay = new ComfySessionEventRelay(
        sessionManager.SignalRuntimeActivity,
        options.RunnerId,
        sessionManager.ComfyHost,
        sessionManager.ComfyPort,
        instanceId,
        sessionManager.GetSessionSealingStatus,
        sessionManager.IsTrackedRuntime,
        artifactUploadService.CaptureSessionRefresh);
    var runtimeTelemetryService = new RuntimeTelemetryService(options.Aria2.DownloadDirectory);
    var gpuPowerLimitService = new GpuPowerCommandExecutor(new GpuPowerLimitService(runtimeTelemetryService));
    var runtimeTelemetryReporter = new ActiveSessionRuntimeTelemetryReporter(
        logicalSessionState.GetActiveSessionId,
        runtimeTelemetryService.TryCaptureSnapshot,
        sessionRuntimeTelemetryRelay.Enqueue,
        options.RuntimeTelemetryInterval,
        sessionId => runtimeIdentities.TryGet(sessionId, out var identity) ? identity : null);
    sessionManager.SessionOwnershipApplied = artifactUploadService.RefreshRecordedVersions;
    sessionManager.PrepareSealedHost = sealedHostPreparation.EnsureApplied;
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
        sessionManager.GetSessionUserId,
        sessionManager.HasSealedSession);

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

    await shutdown.RunAsync(
        commandListener: cancellationToken => ServiceClientManager.CommandListenerLoopAsync(
            commandClient,
            sessionSink,
            options.RunnerId,
            instanceId,
            options.RegistrationMinBackoff,
            options.RegistrationMaxBackoff,
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
            cancellationToken),

        runtimeTelemetry: runtimeTelemetryReporter.RunAsync,
        sealedStorageEviction: cancellationToken => new SealedStorageEvictor(
            options.Session.Sealing,
            sealedStorageProbe,
            logicalSessionState.GetActiveSessionId,
            artifactUploadService.EvictUploadedArtifacts,
            isSessionSealed: sessionManager.IsSessionSealed).RunAsync(cancellationToken),

        statusFile: cancellationToken =>
        {
            var statusFilePath = RunnerStatusFileWriter.ResolvePath(Environment.GetEnvironmentVariable);
            return statusFilePath is null ? Task.CompletedTask : new RunnerStatusFileWriter(
                statusFilePath,
                options.RunnerId,
                instanceId,
                version,
                options.SupportedEditorTemplates,
                logicalSessionState.GetActiveSessionId,
                sealedStorage: () => sealedStorageProbe.Probe(options.Session.Sealing)).RunAsync(cancellationToken);
        },

        stopSessions: async () =>
        {
            try
            {
                await sessionManager.DisposeAsync();
            }
            catch (Exception ex)
            {
                RunnerLog.Error($"Session shutdown failed; errorClass={ex.GetType().Name}.");
                throw;
            }
        });

    RunnerLog.Info("Runner agent exiting.");
    return 0;
}

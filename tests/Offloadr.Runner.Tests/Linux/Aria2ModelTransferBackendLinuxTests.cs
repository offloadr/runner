using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public partial class Aria2ModelTransferBackendLinuxTests
{
    [Test]
    [Category("RealAria2")]
    public async Task RealAria2_PausedDeterministicTransfer_WithMetalink_ReplacesUriAndCompletes()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand(GetAria2BinaryPath());

        var root = Path.Combine(
            Path.GetTempPath(),
            $"runneragent-real-aria2-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var content = Enumerable.Range(0, (2 * 1024 * 1024) + 123)
                .Select(static value => unchecked((byte)value))
                .ToArray();
            await using var source = new RangeHttpServer(content);
            var rpcPort = GetAvailableTcpPort();
            var downloadDirectory = Path.Combine(root, "downloads");
            var stateDirectory = Path.Combine(root, "state");
            Directory.CreateDirectory(downloadDirectory);
            Directory.CreateDirectory(stateDirectory);
            var settings = Aria2Settings.FromEnvironment(name => name switch
            {
                "ARIA2_BINARY" => GetAria2BinaryPath(),
                "ARIA2_DOWNLOAD_DIR" => downloadDirectory,
                "ARIA2_STATE_DIR" => stateDirectory,
                "ARIA2_RPC_PORT" => rpcPort.ToString(),
                "ARIA2_RPC_SECRET" => "real-aria2-test-secret",
                "ARIA2_MAX_CONNECTION_PER_SERVER" => "4",
                "ARIA2_SPLIT" => "2",
                "ARIA2_MIN_SPLIT_SIZE" => "1M",
                "ARIA2_FILE_ALLOCATION" => "none",
                "ARIA2_CHECK_INTEGRITY" => "false",
                "ARIA2_READY_TIMEOUT_SEC" => "10",
                _ => null
            }, () => "unused");

            var destinationPath = Path.Combine(downloadDirectory, "model.bin");
            await using (var placeholder = File.Create(destinationPath))
            {
                placeholder.SetLength(content.Length);
            }

            var identity = ModelTransferIdentity.Create(
                "real-aria2-model",
                destinationPath,
                content.Length);
            await using var backend = new Aria2DownloadBackend(settings);
            await backend.StartAsync(CancellationToken.None);

            var oldUri = source.GetUri("model.bin?token=old");
            var renewedUri = source.GetUri("model.bin?token=renewed");
            var metalink = Encoding.UTF8.GetBytes(
                $"<metalink xmlns=\"urn:ietf:params:xml:ns:metalink\"><file name=\"model.bin\"><size>{content.Length}</size><url>{oldUri}</url></file></metalink>");
            var handles = await backend.CreateAsync(
                new ModelTransferCreateRequest(
                    DestinationPath: destinationPath,
                    ExpectedLength: content.Length,
                    SourceUris: [oldUri],
                    MetalinkContent: metalink,
                    PreferredIdentifier: identity.DeterministicIdentifier,
                    StartPaused: true,
                    QueuePosition: null,
                    ResumeMode: ModelTransferResumeMode.InitializeEmpty),
                CancellationToken.None);

            Assert.That(handles, Has.Count.EqualTo(1));
            Assert.That(handles[0].ToString(), Is.EqualTo(identity.DeterministicIdentifier));
            var paused = await backend.GetStatusAsync(handles[0], CancellationToken.None);
            Assert.That(paused.Status, Is.EqualTo("paused").IgnoreCase);

            await backend.ChangeUriAsync(
                handles[0],
                [oldUri],
                [renewedUri],
                CancellationToken.None);
            await backend.MoveToFrontAsync(handles[0], CancellationToken.None);
            await backend.SaveSessionAsync(CancellationToken.None);
            await backend.UnpauseAsync(handles[0], CancellationToken.None);

            ModelTransferSnapshot completed;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                while (true)
                {
                    completed = await backend.GetStatusAsync(handles[0], timeout.Token);
                    if (completed.IsComplete)
                    {
                        break;
                    }

                    await Task.Delay(100, timeout.Token);
                }
            }

            Assert.Multiple(() =>
            {
                Assert.That(completed.Bitfield, Is.Not.Empty);
                Assert.That(completed.TotalLength, Is.EqualTo(content.Length));
                Assert.That(File.ReadAllBytes(destinationPath), Is.EqualTo(content));
                Assert.That(
                    source.RequestTargets.Any(target => target.Contains("token=renewed", StringComparison.Ordinal)),
                    Is.True);
            });

            await backend.RemoveAsync(handles[0], CancellationToken.None);
            Assert.That(
                async () => await backend.GetStatusAsync(handles[0], CancellationToken.None),
                Throws.InstanceOf<ModelTransferStatusUnavailableException>());
            Assert.That(
                async () => await backend.ForcePauseAsync(handles[0], CancellationToken.None),
                Throws.InstanceOf<ModelTransferStatusUnavailableException>());
            Assert.That(
                async () => await backend.MoveToFrontAsync(handles[0], CancellationToken.None),
                Throws.InstanceOf<ModelTransferStatusUnavailableException>());

            var removablePath = Path.Combine(downloadDirectory, "removable.bin");
            await using (var placeholder = File.Create(removablePath))
            {
                placeholder.SetLength(content.Length);
            }

            var removableIdentity = ModelTransferIdentity.Create(
                "real-aria2-removable",
                removablePath,
                content.Length);
            var removableHandles = await backend.CreateAsync(
                new ModelTransferCreateRequest(
                    DestinationPath: removablePath,
                    ExpectedLength: content.Length,
                    SourceUris: [renewedUri],
                    MetalinkContent: null,
                    PreferredIdentifier: removableIdentity.DeterministicIdentifier,
                    StartPaused: true,
                    QueuePosition: null,
                    ResumeMode: ModelTransferResumeMode.InitializeEmpty),
                CancellationToken.None);
            await backend.RemoveAsync(removableHandles.Single(), CancellationToken.None);

            Assert.That(
                async () => await backend.RemoveAsync(removableHandles.Single(), CancellationToken.None),
                Throws.InstanceOf<ModelTransferStatusUnavailableException>());
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    [Category("RealAria2")]
    [CancelAfter(60000)]
    public async Task RealAria2_NativeShim_PausedOpenGatesPiecesAndMmapWaitsForCompletion()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");
        LinuxTestPrerequisites.RequireCommand(GetAria2BinaryPath());

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(options => options.SingleLine = true);
            builder.SetMinimumLevel(LogLevel.Information);
        });
        RunnerLog.Configure(loggerFactory);

        var root = Path.Combine(
            Path.GetTempPath(),
            $"runneragent-real-aria2-vfs-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            var stateDirectory = Path.Combine(root, "state");
            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(stateDirectory);
            var content = Enumerable.Range(0, (8 * 1024 * 1024) + 123)
                .Select(static value => unchecked((byte)(value * 31 % 251)))
                .ToArray();
            BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(0, 8), 2);
            content[8] = (byte)'{';
            content[9] = (byte)'}';
            await using var source = new RangeHttpServer(
                content,
                responseChunkDelay: TimeSpan.FromMilliseconds(15));
            var settings = CreateSettings(
                root,
                GetAvailableTcpPort(),
                maxConnectionsPerServer: 1,
                split: 1);
            var modelPath = Path.Combine(modelsRoot, "model.safetensors");
            var request = new ModelDownloadRequest
            {
                ModelId = "real-native-model",
                Filename = Path.GetFileName(modelPath),
                DestinationPath = modelPath,
                SizeBytes = content.Length,
                SourceUrl = source.GetUri("model.safetensors?token=current")
            };
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);

            await using var backend = new Aria2DownloadBackend(settings);
            await backend.StartAsync(CancellationToken.None);
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [modelsRoot]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads("real-session", [request], replaceExisting: true);

            var opened = await coordinator.OpenAsync(
                modelPath,
                "real-session",
                CancellationToken.None);
            var initiallyPaused = await backend.GetStatusAsync(
                new ModelTransferHandle(identity.DeterministicIdentifier),
                CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(opened.Disposition, Is.EqualTo(ModelHydrationOpenDisposition.RangeManaged));
                Assert.That(new FileInfo(modelPath).Length, Is.EqualTo(content.Length));
                Assert.That(initiallyPaused.Status, Is.EqualTo("paused").IgnoreCase);
                Assert.That(initiallyPaused.CompletedLength, Is.Zero);
            });
            await coordinator.ReleaseAsync(opened.LeaseId);

            var socketPath = Path.Combine(root, "model-fetch.sock");
            await using var ipcServer = new VfsIpcServer(
                socketPath,
                async (ipcRequest, cancellationToken) =>
                {
                    switch (ipcRequest.Operation)
                    {
                        case VfsIpcOperation.Open:
                            {
                                var result = await coordinator.OpenAsync(
                                    ipcRequest.Path,
                                    ipcRequest.SessionId,
                                    cancellationToken);
                                return result.Disposition == ModelHydrationOpenDisposition.RangeManaged
                                    ? new VfsIpcResponse(
                                        VfsIpcStatus.Success,
                                        VfsIpcOpenDisposition.RangeManaged,
                                        result.LeaseId,
                                        result.TransferEpoch,
                                        result.ExpectedLength,
                                        result.DeviceId,
                                        result.Inode)
                                    : VfsIpcResponse.Error(VfsIpcStatus.NotManaged);
                            }
                        case VfsIpcOperation.EnsureRange:
                            {
                                var result = await coordinator.EnsureRangeAsync(
                                    ipcRequest.LeaseId,
                                    ipcRequest.TransferEpoch,
                                    ipcRequest.Offset,
                                    ipcRequest.Length,
                                    cancellationToken);
                                return VfsIpcResponse.Success(result.TransferEpoch);
                            }
                        case VfsIpcOperation.EnsureComplete:
                            {
                                var result = await coordinator.EnsureCompleteAsync(
                                    ipcRequest.LeaseId,
                                    ipcRequest.TransferEpoch,
                                    ipcRequest.Reason,
                                    cancellationToken);
                                return VfsIpcResponse.Success(result.TransferEpoch);
                            }
                        case VfsIpcOperation.Release:
                            await coordinator.ReleaseAsync(ipcRequest.LeaseId);
                            return VfsIpcResponse.Success();
                        default:
                            return VfsIpcResponse.Error(VfsIpcStatus.InvalidHeader);
                    }
                });
            ipcServer.Start();

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "real-aria2-probe.c");
            var probePath = Path.Combine(root, "real-aria2-probe");
            await VfsShimLinuxTests.CompileShimAsync(shimPath);
            await WriteRealAria2ProbeAsync(probeSourcePath);
            await VfsShimLinuxTests.RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);
            var environment = new Dictionary<string, string?>
            {
                ["LD_PRELOAD"] = shimPath,
                ["VFS_SOCKET"] = socketPath,
                ["VFS_ROOTS"] = modelsRoot,
                ["VFS_EXTS"] = ".safetensors",
                ["VFS_SESSION_ID"] = "real-session",
                ["VFS_LOG"] = "1"
            };

            var rangeResult = await VfsShimLinuxTests.RunProcessAsync(
                probePath,
                ["range", modelPath, content.Length.ToString(), content[^1].ToString()],
                root,
                environment);
            var partiallyReady = await WaitForStatusAsync(
                backend,
                new ModelTransferHandle(identity.DeterministicIdentifier),
                snapshot => string.Equals(snapshot.Status, "paused", StringComparison.OrdinalIgnoreCase) &&
                            snapshot.CompletedLength > 0,
                TimeSpan.FromSeconds(10));
            var readablePrefix = File.ReadAllBytes(modelPath).AsSpan(0, 10).ToArray();

            Assert.Multiple(() =>
            {
                Assert.That(rangeResult.Stdout.Trim(), Is.EqualTo("range-ready"));
                Assert.That(partiallyReady.IsComplete, Is.False);
                Assert.That(partiallyReady.CompletedLength, Is.LessThan(content.Length));
                Assert.That(
                    DemandAwareModelHydrationCoordinator.DecodeLoadedPieces(
                        partiallyReady.Bitfield,
                        partiallyReady.NumPieces),
                    Does.Contain(0));
                Assert.That(readablePrefix, Is.EqualTo(content.AsSpan(0, 10).ToArray()));
            });

            VfsShimLinuxTests.ProcessResult mappingResult;
            try
            {
                mappingResult = await VfsShimLinuxTests.RunProcessAsync(
                    probePath,
                    ["map", modelPath, content.Length.ToString(), content[^1].ToString()],
                    root,
                    environment);
            }
            catch (AssertionException ex)
            {
                string transferDiagnostic;
                try
                {
                    var stalled = await backend.GetStatusAsync(
                        new ModelTransferHandle(identity.DeterministicIdentifier),
                        CancellationToken.None);
                    transferDiagnostic =
                        $"transfer status={stalled.Status} complete={stalled.IsComplete} " +
                        $"completed={stalled.CompletedLength}/{stalled.TotalLength} " +
                        $"pieces={stalled.NumPieces} bitfield={stalled.Bitfield} " +
                        $"error={stalled.ErrorCode}:{stalled.ErrorMessage}";
                }
                catch (Exception diagnosticException)
                {
                    transferDiagnostic =
                        $"transfer status unavailable: {diagnosticException.GetType().Name}: " +
                        diagnosticException.Message;
                }

                Assert.Fail(
                    $"{ex.Message}\n" +
                    transferDiagnostic);
                throw;
            }
            var completed = await WaitForStatusAsync(
                backend,
                new ModelTransferHandle(identity.DeterministicIdentifier),
                static snapshot => snapshot.IsComplete,
                TimeSpan.FromSeconds(20));

            Assert.Multiple(() =>
            {
                Assert.That(mappingResult.Stdout.Trim(), Is.EqualTo("mapping-ready"));
                Assert.That(completed.IsComplete, Is.True);
                Assert.That(File.ReadAllBytes(modelPath), Is.EqualTo(content));
                Assert.That(
                    File.ReadAllBytes(modelPath).AsSpan(0, 10).ToArray(),
                    Is.EqualTo(readablePrefix));
            });
        }
        finally
        {
            RunnerLog.Configure(null);
            TryDelete(root);
        }
    }

    [Test]
    [Category("RealAria2")]
    [CancelAfter(60000)]
    public async Task RealAria2_PartialTransfer_ReattachesAcrossProcessRestartWithoutEpochChange()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand(GetAria2BinaryPath());

        var root = Path.Combine(
            Path.GetTempPath(),
            $"runneragent-real-aria2-restart-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            var stateDirectory = Path.Combine(root, "state");
            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(stateDirectory);
            var content = new byte[(6 * 1024 * 1024) + 31];
            for (var index = 0; index < content.Length; index++)
            {
                content[index] = unchecked((byte)(index * 17 % 251));
            }

            BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(0, 8), 2);
            content[8] = (byte)'{';
            content[9] = (byte)'}';
            await using var source = new RangeHttpServer(
                content,
                responseChunkDelay: TimeSpan.FromMilliseconds(15));
            var modelPath = Path.Combine(modelsRoot, "restart.safetensors");
            var request = new ModelDownloadRequest
            {
                ModelId = "restart-model",
                Filename = Path.GetFileName(modelPath),
                DestinationPath = modelPath,
                SizeBytes = content.Length,
                SourceUrl = source.GetUri("restart.safetensors?token=current")
            };
            var identity = ModelTransferIdentity.Create(
                request.ModelId,
                request.DestinationPath,
                request.SizeBytes);
            var handle = new ModelTransferHandle(identity.DeterministicIdentifier);
            long originalEpoch;
            byte[] originalPrefix;

            var firstSettings = CreateSettings(
                root,
                GetAvailableTcpPort(),
                maxConnectionsPerServer: 1,
                split: 1);
            await using (var firstBackend = new Aria2DownloadBackend(
                             firstSettings,
                             useProcessWatchdog: false))
            {
                await firstBackend.StartAsync(CancellationToken.None);
                await using (var firstCoordinator = new DemandAwareModelHydrationCoordinator(
                                 firstBackend,
                                 new ModelHydrationRuntimeCapabilities(
                                     "synthetic-editor",
                                     SupportsPersistentRangeHydration: true,
                                     PersistentModelRoots: [modelsRoot]),
                                 stateDirectory))
                {
                    await firstCoordinator.InitializeAsync(CancellationToken.None);
                    firstCoordinator.RegisterDownloads(
                        "restart-session",
                        [request],
                        replaceExisting: true);
                    var opened = await firstCoordinator.OpenAsync(
                        modelPath,
                        "restart-session",
                        CancellationToken.None);
                    await firstCoordinator.EnsureRangeAsync(
                        opened.LeaseId,
                        opened.TransferEpoch,
                        0,
                        10,
                        CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                    await firstCoordinator.ReleaseAsync(opened.LeaseId);
                    var paused = await WaitForStatusAsync(
                        firstBackend,
                        handle,
                        snapshot => string.Equals(
                                        snapshot.Status,
                                        "paused",
                                        StringComparison.OrdinalIgnoreCase) &&
                                    snapshot.CompletedLength > 0 &&
                                    !snapshot.IsComplete,
                        TimeSpan.FromSeconds(10));
                    originalEpoch = opened.TransferEpoch;
                    originalPrefix = File.ReadAllBytes(modelPath).AsSpan(0, 10).ToArray();
                    Assert.That(
                        DemandAwareModelHydrationCoordinator.DecodeLoadedPieces(
                            paused.Bitfield,
                            paused.NumPieces),
                        Does.Contain(0));
                }
            }

            var secondSettings = CreateSettings(
                root,
                GetAvailableTcpPort(),
                maxConnectionsPerServer: 1,
                split: 1);
            await using var secondBackend = new Aria2DownloadBackend(
                secondSettings,
                useProcessWatchdog: false);
            await secondBackend.StartAsync(CancellationToken.None);
            await using var secondCoordinator = new DemandAwareModelHydrationCoordinator(
                secondBackend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [modelsRoot]),
                stateDirectory);
            await secondCoordinator.InitializeAsync(CancellationToken.None);
            secondCoordinator.RegisterDownloads(
                "restart-session",
                [request],
                replaceExisting: true);
            var reopened = await secondCoordinator.OpenAsync(
                modelPath,
                "restart-session",
                CancellationToken.None);
            var restored = await secondBackend.GetStatusAsync(handle, CancellationToken.None);

            Assert.Multiple(() =>
            {
                Assert.That(reopened.TransferEpoch, Is.EqualTo(originalEpoch));
                Assert.That(restored.Handle, Is.EqualTo(handle));
                Assert.That(restored.Status, Is.EqualTo("paused").IgnoreCase);
                Assert.That(restored.IsComplete, Is.False);
                Assert.That(
                    File.ReadAllBytes(modelPath).AsSpan(0, 10).ToArray(),
                    Is.EqualTo(originalPrefix));
            });

            try
            {
                await secondCoordinator.EnsureCompleteAsync(
                    reopened.LeaseId,
                    reopened.TransferEpoch,
                    "restart-integration",
                    CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            }
            catch (TimeoutException exception)
            {
                string statusDiagnostic;
                try
                {
                    var stalled = await secondBackend.GetStatusAsync(handle, CancellationToken.None);
                    statusDiagnostic =
                        $"status={stalled.Status} " +
                        $"completed={stalled.CompletedLength}/{stalled.TotalLength} " +
                        $"pieceLength={stalled.PieceLength} pieces={stalled.NumPieces} " +
                        $"bitfield={stalled.Bitfield}";
                }
                catch (Exception statusException)
                {
                    statusDiagnostic =
                        $"statusRpcFailure={statusException.GetType().Name}: {statusException.Message}";
                }

                Assert.Fail(
                    $"Reattached transfer did not complete: {statusDiagnostic} " +
                    $"firstRpcPort={firstSettings.RpcPort} secondRpcPort={secondSettings.RpcPort} " +
                    $"requests={source.RequestTargets.Count} " +
                    $"controlFile={File.Exists($"{modelPath}.aria2")}. {exception.Message}");
            }

            await secondCoordinator.ReleaseAsync(reopened.LeaseId);
            var completed = await secondBackend.GetStatusAsync(handle, CancellationToken.None);
            Assert.Multiple(() =>
            {
                Assert.That(completed.IsComplete, Is.True);
                Assert.That(
                    DemandAwareModelHydrationCoordinator.DecodeLoadedPieces(
                        completed.Bitfield,
                        completed.NumPieces),
                    Does.Contain(0));
                Assert.That(File.ReadAllBytes(modelPath), Is.EqualTo(content));
                Assert.That(
                    File.ReadAllBytes(modelPath).AsSpan(0, 10).ToArray(),
                    Is.EqualTo(originalPrefix));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Test]
    [Category("RealAria2")]
    [CancelAfter(60000)]
    public async Task RealAria2_BlockedRangePreemptsStickyTransferAndOnlyOneIsActive()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand(GetAria2BinaryPath());

        var root = Path.Combine(
            Path.GetTempPath(),
            $"runneragent-real-aria2-preemption-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            var stateDirectory = Path.Combine(root, "state");
            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(stateDirectory);
            var content = new byte[(8 * 1024 * 1024) + 37];
            for (var index = 0; index < content.Length; index++)
            {
                content[index] = unchecked((byte)(index * 13 % 251));
            }

            BinaryPrimitives.WriteUInt64LittleEndian(content.AsSpan(0, 8), 2);
            content[8] = (byte)'{';
            content[9] = (byte)'}';
            await using var source = new RangeHttpServer(
                content,
                responseChunkDelay: TimeSpan.FromMilliseconds(15));
            var stickyRequest = new ModelDownloadRequest
            {
                ModelId = "sticky-model",
                Filename = "sticky.bin",
                DestinationPath = Path.Combine(modelsRoot, "sticky.bin"),
                SizeBytes = content.Length,
                SourceUrl = source.GetUri("sticky.bin")
            };
            var rangeRequest = new ModelDownloadRequest
            {
                ModelId = "range-model",
                Filename = "range.safetensors",
                DestinationPath = Path.Combine(modelsRoot, "range.safetensors"),
                SizeBytes = content.Length,
                SourceUrl = source.GetUri("range.safetensors")
            };
            var stickyHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                    stickyRequest.ModelId,
                    stickyRequest.DestinationPath,
                    stickyRequest.SizeBytes).DeterministicIdentifier);
            var rangeHandle = new ModelTransferHandle(
                ModelTransferIdentity.Create(
                    rangeRequest.ModelId,
                    rangeRequest.DestinationPath,
                    rangeRequest.SizeBytes).DeterministicIdentifier);
            var settings = CreateSettings(
                root,
                GetAvailableTcpPort(),
                maxConnectionsPerServer: 1,
                split: 1);
            await using var backend = new Aria2DownloadBackend(settings);
            await backend.StartAsync(CancellationToken.None);
            await using var coordinator = new DemandAwareModelHydrationCoordinator(
                backend,
                new ModelHydrationRuntimeCapabilities(
                    "synthetic-editor",
                    SupportsPersistentRangeHydration: true,
                    PersistentModelRoots: [modelsRoot]),
                stateDirectory);
            await coordinator.InitializeAsync(CancellationToken.None);
            coordinator.RegisterDownloads(
                "preemption-session",
                [stickyRequest, rangeRequest],
                replaceExisting: true);

            var stickyOpen = await coordinator.OpenAsync(
                stickyRequest.DestinationPath,
                "preemption-session",
                CancellationToken.None);
            await coordinator.EnsureRangeAsync(
                stickyOpen.LeaseId,
                stickyOpen.TransferEpoch,
                0,
                10,
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForStatusAsync(
                backend,
                stickyHandle,
                snapshot => string.Equals(snapshot.Status, "active", StringComparison.OrdinalIgnoreCase) &&
                            !snapshot.IsComplete,
                TimeSpan.FromSeconds(5));

            var rangeOpen = await coordinator.OpenAsync(
                rangeRequest.DestinationPath,
                "preemption-session",
                CancellationToken.None);
            var rangeWaiter = coordinator.EnsureRangeAsync(
                rangeOpen.LeaseId,
                rangeOpen.TransferEpoch,
                0,
                10,
                CancellationToken.None);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            var observedPreemption = false;
            while (DateTime.UtcNow < deadline)
            {
                var stickyStatus = await backend.GetStatusAsync(stickyHandle, CancellationToken.None);
                var rangeStatus = await backend.GetStatusAsync(rangeHandle, CancellationToken.None);
                var activeCount = new[] { stickyStatus, rangeStatus }.Count(snapshot =>
                    string.Equals(snapshot.Status, "active", StringComparison.OrdinalIgnoreCase));
                Assert.That(activeCount, Is.LessThanOrEqualTo(1));
                if (string.Equals(stickyStatus.Status, "paused", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(rangeStatus.Status, "active", StringComparison.OrdinalIgnoreCase))
                {
                    observedPreemption = true;
                    break;
                }

                await Task.Delay(25);
            }

            await rangeWaiter.WaitAsync(TimeSpan.FromSeconds(15));
            await coordinator.ReleaseAsync(rangeOpen.LeaseId);
            Assert.That(observedPreemption, Is.True);

            var stickyCompleted = await WaitForStatusAsync(
                backend,
                stickyHandle,
                static snapshot => snapshot.IsComplete,
                TimeSpan.FromSeconds(20));
            var rangePartial = await backend.GetStatusAsync(rangeHandle, CancellationToken.None);
            await coordinator.ReleaseAsync(stickyOpen.LeaseId);

            Assert.Multiple(() =>
            {
                Assert.That(stickyCompleted.IsComplete, Is.True);
                Assert.That(rangePartial.IsComplete, Is.False);
                Assert.That(rangePartial.CompletedLength, Is.GreaterThan(0));
            });
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static int GetAvailableTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string GetAria2BinaryPath()
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("RUNNER_NATIVE_TESTS_REQUIRED"),
                "1",
                StringComparison.Ordinal))
        {
            return "/usr/bin/aria2c";
        }

        return Environment.GetEnvironmentVariable("RUNNER_TEST_ARIA2_BINARY")
            ?? "/usr/bin/aria2c";
    }

    private static Aria2Settings CreateSettings(
        string root,
        int rpcPort,
        int maxConnectionsPerServer = 4,
        int split = 2)
    {
        var downloadDirectory = Path.Combine(root, "downloads");
        var stateDirectory = Path.Combine(root, "state");
        Directory.CreateDirectory(downloadDirectory);
        Directory.CreateDirectory(stateDirectory);
        return Aria2Settings.FromEnvironment(name => name switch
        {
            "ARIA2_BINARY" => GetAria2BinaryPath(),
            "ARIA2_DOWNLOAD_DIR" => downloadDirectory,
            "ARIA2_STATE_DIR" => stateDirectory,
            "ARIA2_RPC_PORT" => rpcPort.ToString(),
            "ARIA2_RPC_SECRET" => "real-aria2-test-secret",
            "ARIA2_MAX_CONNECTION_PER_SERVER" => maxConnectionsPerServer.ToString(),
            "ARIA2_SPLIT" => split.ToString(),
            "ARIA2_MIN_SPLIT_SIZE" => "1M",
            "ARIA2_FILE_ALLOCATION" => "none",
            "ARIA2_CHECK_INTEGRITY" => "false",
            "ARIA2_READY_TIMEOUT_SEC" => "10",
            _ => null
        }, () => "unused");
    }

    private static async Task<ModelTransferSnapshot> WaitForStatusAsync(
        IModelTransferBackend backend,
        ModelTransferHandle handle,
        Func<ModelTransferSnapshot, bool> predicate,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            var snapshot = await backend.GetStatusAsync(handle, cancellation.Token);
            if (predicate(snapshot))
            {
                return snapshot;
            }

            await Task.Delay(100, cancellation.Token);
        }
    }

    private static Task WriteRealAria2ProbeAsync(string path)
        => File.WriteAllTextAsync(path, """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <sys/mman.h>
            #include <sys/stat.h>
            #include <unistd.h>

            int main(int argc, char **argv) {
                if (argc != 5) return 10;
                const char *mode = argv[1];
                const char *path = argv[2];
                long expected = strtol(argv[3], NULL, 10);
                int expected_last = atoi(argv[4]);
                struct stat info;
                fprintf(stderr, "probe stage=stat mode=%s\n", mode);
                fflush(stderr);
                if (stat(path, &info) != 0 || info.st_size != expected) return 11;

                fprintf(stderr, "probe stage=open mode=%s\n", mode);
                fflush(stderr);
                int fd = open(path, O_RDONLY);
                if (fd < 0) return 12;
                if (strcmp(mode, "range") == 0) {
                    unsigned char prefix[10];
                    fprintf(stderr, "probe stage=pread\n");
                    fflush(stderr);
                    if (pread(fd, prefix, sizeof(prefix), 0) != sizeof(prefix)) return 13;
                    if (prefix[0] != 2 || prefix[8] != '{' || prefix[9] != '}') return 14;
                    fprintf(stderr, "probe stage=close-range\n");
                    fflush(stderr);
                    close(fd);
                    puts("range-ready");
                    return 0;
                }

                if (strcmp(mode, "map") != 0) return 15;
                fprintf(stderr, "probe stage=mmap\n");
                fflush(stderr);
                unsigned char *mapping = mmap(NULL, expected, PROT_READ, MAP_PRIVATE, fd, 0);
                if (mapping == MAP_FAILED) return 16;
                if (mapping[0] != 2 || mapping[8] != '{' ||
                    mapping[expected - 1] != expected_last) return 17;
                fprintf(stderr, "probe stage=close-map\n");
                fflush(stderr);
                munmap(mapping, expected);
                close(fd);
                puts("mapping-ready");
                return 0;
            }
            """);

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best effort test cleanup.
        }
    }

    private sealed class RangeHttpServer : IAsyncDisposable
    {
        private readonly byte[] _content;
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _listenTask;
        private readonly TimeSpan _responseChunkDelay;

        public RangeHttpServer(
            byte[] content,
            TimeSpan? responseChunkDelay = null)
        {
            _content = content;
            _responseChunkDelay = responseChunkDelay ?? TimeSpan.Zero;
            var port = GetAvailableTcpPort();
            BaseUri = new Uri($"http://127.0.0.1:{port}/");
            _listener.Prefixes.Add(BaseUri.AbsoluteUri);
            _listener.Start();
            _listenTask = Task.Run(() => ListenAsync(_shutdown.Token));
        }

        public Uri BaseUri { get; }
        public ConcurrentQueue<string> RequestTargets { get; } = new();

        public string GetUri(string relativePath) => new Uri(BaseUri, relativePath).AbsoluteUri;

        private async Task ListenAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = Task.Run(() => RespondAsync(context), CancellationToken.None);
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            try
            {
                RequestTargets.Enqueue(context.Request.RawUrl ?? string.Empty);
                var (start, end, partial) = ParseRange(
                    context.Request.Headers["Range"],
                    _content.Length);
                var length = end - start + 1;
                context.Response.StatusCode = partial
                    ? (int)HttpStatusCode.PartialContent
                    : (int)HttpStatusCode.OK;
                context.Response.ContentLength64 = length;
                context.Response.Headers["Accept-Ranges"] = "bytes";
                if (partial)
                {
                    context.Response.Headers["Content-Range"] =
                        $"bytes {start}-{end}/{_content.Length}";
                }

                if (!string.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    const int chunkSize = 64 * 1024;
                    var offset = start;
                    var remaining = length;
                    while (remaining > 0)
                    {
                        var count = Math.Min(chunkSize, remaining);
                        await context.Response.OutputStream.WriteAsync(
                            _content.AsMemory(offset, count));
                        offset += count;
                        remaining -= count;
                        if (_responseChunkDelay > TimeSpan.Zero && remaining > 0)
                        {
                            await Task.Delay(_responseChunkDelay);
                        }
                    }
                }
            }
            finally
            {
                context.Response.Close();
            }
        }

        private static (int Start, int End, bool Partial) ParseRange(
            string? range,
            int totalLength)
        {
            if (string.IsNullOrWhiteSpace(range) ||
                !range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            {
                return (0, totalLength - 1, false);
            }

            var values = range["bytes=".Length..].Split('-', 2);
            if (!int.TryParse(values[0], out var start))
            {
                return (0, totalLength - 1, false);
            }

            var end = values.Length == 2 && int.TryParse(values[1], out var parsedEnd)
                ? parsedEnd
                : totalLength - 1;
            return (
                Math.Clamp(start, 0, totalLength - 1),
                Math.Clamp(end, start, totalLength - 1),
                true);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            try
            {
                await _listenTask;
            }
            catch (HttpListenerException)
            {
                // Listener shutdown.
            }

            _listener.Close();
            _shutdown.Dispose();
        }
    }
}

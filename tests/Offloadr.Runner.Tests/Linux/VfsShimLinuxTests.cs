using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public class VfsShimLinuxTests
{
    [Test]
    public async Task WritableOpen_WatchedArtifactPaths_FallsThroughToLibc()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var outputRoot = Path.Combine(root, "output");
            Directory.CreateDirectory(outputRoot);
            var openPath = Path.Combine(outputRoot, "open-output.png");
            var fopenPath = Path.Combine(outputRoot, "fopen-output.png");
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteWritableProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.NotManaged));
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [openPath, fopenPath, outputRoot],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = outputRoot,
                    ["VFS_EXTS"] = ".png",
                    ["VFS_LOG"] = "0"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(File.ReadAllText(openPath), Is.EqualTo("open"));
                Assert.That(File.ReadAllText(fopenPath), Is.EqualTo("fopen"));
                Assert.That(requests, Has.Count.EqualTo(2));
                Assert.That(requests.All(static request => request.WriteAccess), Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Fopen64_ReadOnlyPath_HydratesPlaceholderBeforeRead()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var inputRoot = Path.Combine(root, "input");
            Directory.CreateDirectory(inputRoot);
            var imagePath = Path.Combine(inputRoot, "uploaded.png");
            await File.WriteAllBytesAsync(imagePath, Array.Empty<byte>());

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReadProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                requests.Add(request);
                await File.WriteAllBytesAsync(request.Path, "hydrated"u8.ToArray());
                return new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.FullReady,
                    0,
                    0,
                    0);
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [imagePath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = $"{inputRoot}/",
                    ["VFS_EXTS"] = ".png",
                    ["VFS_LOG"] = "0"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout, Is.EqualTo("hydrated"));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Path, Is.EqualTo(imagePath));
                Assert.That(requests[0].Operation, Is.EqualTo(VfsIpcOperation.Open));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task FortifiedOpenSymbols_StillRouteThroughHydrationIpc()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "fortified.bin");
            await File.WriteAllTextAsync(modelPath, "model");
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "fortified-open-probe.c");
            var probePath = Path.Combine(root, "fortified-open-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteFortifiedOpenProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                ["-O2", probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.FullReady,
                    0,
                    0,
                    0));
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelsRoot, Path.GetFileName(modelPath), modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(requests, Has.Count.EqualTo(4));
                Assert.That(requests.All(request => request.Path == modelPath), Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Fopen64_ExistingModelPath_StillValidatesThroughIpc()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "local.safetensors");
            await File.WriteAllTextAsync(modelPath, "local-model");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReadProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.FullReady,
                    0,
                    0,
                    0));
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".safetensors",
                    ["VFS_LOG"] = "1"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout, Is.EqualTo("local-model"));
                Assert.That(result.Stderr, Does.Not.Contain("request failed"));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Path, Is.EqualTo(modelPath));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Fopen64_ExistingInputPath_StillHydratesThroughIpc()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var inputRoot = Path.Combine(root, "input");
            Directory.CreateDirectory(inputRoot);
            var imagePath = Path.Combine(inputRoot, "uploaded.png");
            await File.WriteAllTextAsync(imagePath, "stale");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReadProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                requests.Add(request);
                await File.WriteAllTextAsync(request.Path, "remote");
                return new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.FullReady,
                    0,
                    0,
                    0);
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [imagePath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = inputRoot,
                    ["VFS_EXTS"] = ".png",
                    ["VFS_LOG"] = "0"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout, Is.EqualTo("remote"));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Path, Is.EqualTo(imagePath));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Fopen64_MaterializedPathWithAriaSidecar_StillHydratesThroughIpc()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "remote.safetensors");
            await File.WriteAllTextAsync(modelPath, "partial");
            await File.WriteAllTextAsync($"{modelPath}.aria2", "pending");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReadProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                requests.Add(request);
                await File.WriteAllTextAsync(request.Path, "hydrated");
                return new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.FullReady,
                    0,
                    0,
                    0);
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".safetensors",
                    ["VFS_LOG"] = "0"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout, Is.EqualTo("hydrated"));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Path, Is.EqualTo(modelPath));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Fopen64_WhenHydrationFails_FailsOpenWithoutReadingPlaceholder()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var inputRoot = Path.Combine(root, "input");
            Directory.CreateDirectory(inputRoot);
            var imagePath = Path.Combine(inputRoot, "uploaded.png");
            await File.WriteAllTextAsync(imagePath, "placeholder");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "probe.c");
            var probePath = Path.Combine(root, "probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReadProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                requests.Add(request);
                throw new IOException("hydration failed");
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [imagePath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = inputRoot,
                    ["VFS_EXTS"] = ".png",
                    ["VFS_LOG"] = "1"
                },
                expectSuccess: false);

            Assert.Multiple(() =>
            {
                Assert.That(result.ExitCode, Is.EqualTo(11));
                Assert.That(result.Stdout, Is.EqualTo(string.Empty));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Path, Is.EqualTo(imagePath));
                Assert.That(File.ReadAllText(imagePath), Is.EqualTo("placeholder"));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Fopen64_WhenPathNotManaged_FallsThroughToRealOpenEnoent()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var sidecarPath = Path.Combine(modelsRoot, "missing.json");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "errno-probe.c");
            var probePath = Path.Combine(root, "errno-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteErrnoProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                requests.Add(request);
                return Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.NotManaged));
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [sidecarPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".json",
                    ["VFS_LOG"] = "1"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("2"));
                Assert.That(requests, Has.Count.EqualTo(1));
                Assert.That(requests[0].Path, Is.EqualTo(sidecarPath));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_RealOpenFailure_ReleasesGrantedLease()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var openPath = Path.Combine(modelsRoot, "open.bin");
            var fopenPath = Path.Combine(modelsRoot, "fopen.bin");
            var swappedPath = Path.Combine(modelsRoot, "swapped.bin");
            var replacementPath = Path.Combine(modelsRoot, "replacement.bin");
            await File.WriteAllBytesAsync(openPath, new byte[8]);
            await File.WriteAllBytesAsync(fopenPath, new byte[8]);
            await File.WriteAllBytesAsync(swappedPath, new byte[8]);
            await File.WriteAllBytesAsync(replacementPath, "replaced"u8.ToArray());
            var openIdentity = ReadIdentity(openPath);
            var fopenIdentity = ReadIdentity(fopenPath);
            var swappedIdentity = ReadIdentity(swappedPath);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "open-failure-probe.c");
            var probePath = Path.Combine(root, "open-failure-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteOpenFailureProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.Open)
                {
                    var isFopen = request.Path.EndsWith("fopen.bin", StringComparison.Ordinal);
                    var isSwapped = request.Path.EndsWith("swapped.bin", StringComparison.Ordinal);
                    if (!isFopen && !isSwapped)
                    {
                        File.Delete(request.Path);
                    }
                    else if (isSwapped)
                    {
                        File.Move(replacementPath, swappedPath, overwrite: true);
                    }

                    var leaseId = isFopen ? 102UL : isSwapped ? 103UL : 101UL;
                    return Task.FromResult(RangeManagedResponse(
                        isFopen ? fopenIdentity : isSwapped ? swappedIdentity : openIdentity,
                        leaseId,
                        transferEpoch: 1,
                        expectedLength: isFopen ? 9 : 8));
                }

                return Task.FromResult(VfsIpcResponse.Success(transferEpoch: 1));
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [openPath, fopenPath, swappedPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                        snapshot.Where(request => request.Operation == VfsIpcOperation.Release)
                            .Select(request => request.LeaseId),
                    Is.EquivalentTo(new ulong[] { 101, 102, 103 }));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_ReleaseFailure_RetriesUntilAcknowledged()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "release-retry.bin");
            await File.WriteAllBytesAsync(modelPath, new byte[8]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "release-retry-probe.c");
            var probePath = Path.Combine(root, "release-retry-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReleaseRetryProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var releaseAttempts = 0;
            var releaseAcknowledged = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                if (request.Operation == VfsIpcOperation.Open)
                {
                    return Task.FromResult(RangeManagedResponse(
                        modelPath,
                        leaseId: 811,
                        transferEpoch: 1,
                        expectedLength: 8));
                }

                if (request.Operation == VfsIpcOperation.Release)
                {
                    var attempt = Interlocked.Increment(ref releaseAttempts);
                    if (attempt >= 2)
                    {
                        releaseAcknowledged.TrySetResult();
                        return Task.FromResult(VfsIpcResponse.Success(transferEpoch: 1));
                    }

                    return Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.ServerError));
                }

                return Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.ServerError));
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "release-retry-session",
                    ["VFS_LOG"] = "0"
                });
            await releaseAcknowledged.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(releaseAttempts, Is.GreaterThanOrEqualTo(2));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_DescriptorInheritance_IsRejectedOrCloseOnExec()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "inheritance.bin");
            await File.WriteAllBytesAsync(modelPath, new byte[8]);
            var spawnOpenAliasPath = Path.Combine(root, "spawn-open.bin");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "inheritance-probe.c");
            var probePath = Path.Combine(root, "inheritance-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteInheritanceProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                return Task.FromResult(request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 812,
                        transferEpoch: 1,
                        expectedLength: 8),
                    VfsIpcOperation.Release => VfsIpcResponse.Success(transferEpoch: 1),
                    _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
                });
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath, spawnOpenAliasPath, modelsRoot],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "inheritance-session",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Open),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_DescriptorPassingThroughScmRights_IsRejected()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "descriptor-message.bin");
            await File.WriteAllBytesAsync(modelPath, new byte[8]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "descriptor-message-probe.c");
            var probePath = Path.Combine(root, "descriptor-message-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteDescriptorMessageProbeAsync(probeSourcePath);
            await RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                return Task.FromResult(request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 813,
                        transferEpoch: 1,
                        expectedLength: 8),
                    VfsIpcOperation.Release => VfsIpcResponse.Success(transferEpoch: 1),
                    _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
                });
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "descriptor-message-session",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Open),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.Any(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_DupOntoExistingBufferedStream_IsRejected()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "buffered-dup.bin");
            var ordinaryPath = Path.Combine(root, "ordinary.bin");
            await File.WriteAllBytesAsync(modelPath, new byte[8]);
            await File.WriteAllTextAsync(ordinaryPath, "ordinary");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "buffered-dup-probe.c");
            var probePath = Path.Combine(root, "buffered-dup-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteBufferedDupProbeAsync(probeSourcePath);
            await RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                return Task.FromResult(request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 814,
                        transferEpoch: 1,
                        expectedLength: 8),
                    VfsIpcOperation.Release => VfsIpcResponse.Success(transferEpoch: 1),
                    _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
                });
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath, ordinaryPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "buffered-dup-session",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Open), Is.EqualTo(1));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Release), Is.EqualTo(1));
                Assert.That(
                    snapshot.Any(request => request.Operation is
                        VfsIpcOperation.EnsureRange or VfsIpcOperation.EnsureComplete),
                    Is.False);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_FreadHydrationFailure_SetsStreamErrorIndicator()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "failed-read.bin");
            await File.WriteAllBytesAsync(modelPath, new byte[8]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "ferror-probe.c");
            var probePath = Path.Combine(root, "ferror-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteFerrorProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                return Task.FromResult(request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 201,
                        transferEpoch: 1,
                        expectedLength: 8),
                    VfsIpcOperation.EnsureRange => VfsIpcResponse.Error(
                        VfsIpcStatus.IoError,
                        transferEpoch: 1),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                });
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.EqualTo(2));
                Assert.That(snapshot.Any(request => request.Operation == VfsIpcOperation.Release), Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_AllSupportedSurfaces_UseRangeAndCompletionProtocol()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "surface.bin");
            var content = "abcdefghijklmnopqrstuvwxyz012345"u8.ToArray();
            await using (var placeholder = File.Create(modelPath))
            {
                placeholder.SetLength(content.Length);
            }

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "surface-probe.c");
            var probePath = Path.Combine(root, "surface-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");
            var lexicalModelPath = Path.Combine(modelsRoot, "..", "models", "surface.bin");

            await CompileShimAsync(shimPath);
            await WriteSurfaceProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.Open)
                {
                    await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);
                }
                else if (request.Operation is VfsIpcOperation.EnsureRange or VfsIpcOperation.EnsureComplete)
                {
                    await File.WriteAllBytesAsync(modelPath, content);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 91,
                        transferEpoch: 4,
                        expectedLength: content.Length),
                    VfsIpcOperation.EnsureRange or
                    VfsIpcOperation.EnsureComplete or
                    VfsIpcOperation.Release => VfsIpcResponse.Success(transferEpoch: 4),
                    _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath, lexicalModelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "surface-session",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Open), Is.GreaterThanOrEqualTo(7));
                Assert.That(snapshot.Any(request => request.Operation == VfsIpcOperation.EnsureRange), Is.True);
                Assert.That(snapshot.Any(request => request.Operation == VfsIpcOperation.EnsureComplete), Is.True);
                Assert.That(
                    snapshot.Any(request =>
                        request.Operation == VfsIpcOperation.EnsureComplete &&
                        request.Reason == "formatted_read"),
                    Is.True);
                Assert.That(
                    snapshot.Any(request =>
                        request.Operation == VfsIpcOperation.EnsureComplete &&
                        request.Reason == "wide_read"),
                    Is.True);
                Assert.That(
                    snapshot.Any(request =>
                        request.Operation == VfsIpcOperation.EnsureComplete &&
                        request.Reason == "wide_formatted_read"),
                    Is.True);
                Assert.That(snapshot.Any(request => request.Operation == VfsIpcOperation.Release), Is.True);
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.Open)
                        .All(request => request.SessionId == "surface-session"),
                    Is.True);
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.Open)
                        .All(request => request.Path == modelPath),
                    Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_OptimizedUnlockedStdioAndPosixAio_GateRequestedRanges()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "async.bin");
            var content = "abcdefghijklmnop"u8.ToArray();
            await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "async-probe.c");
            var probePath = Path.Combine(root, "async-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteOptimizedUnlockedAndAioProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                ["-O2", probeSourcePath, "-o", probePath, "-lrt"],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.Open)
                {
                    await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);
                }
                else if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await using var target = new FileStream(
                        modelPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    target.Position = request.Offset;
                    await target.WriteAsync(content.AsMemory((int)request.Offset, (int)request.Length));
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 915,
                        transferEpoch: 2,
                        expectedLength: content.Length),
                    VfsIpcOperation.EnsureRange or VfsIpcOperation.Release =>
                        VfsIpcResponse.Success(transferEpoch: 2),
                    _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "async-session",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            var ranges = snapshot
                .Where(request => request.Operation == VfsIpcOperation.EnsureRange)
                .Select(request => (request.Offset, request.Length))
                .ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(ranges, Does.Contain((0L, 1L)));
                Assert.That(ranges, Does.Contain((1L, 1L)));
                Assert.That(ranges, Does.Contain((0L, 4L)));
                Assert.That(ranges, Does.Contain((2L, 2L)));
                Assert.That(ranges, Does.Contain((4L, 2L)));
                Assert.That(ranges, Does.Contain((6L, 2L)));
                Assert.That(ranges, Does.Contain((8L, 2L)));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(9));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_StdioLockOrder_AvoidsFlockfileDeadlock()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "stdio-lock.bin");
            var content = "ab"u8.ToArray();
            await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "stdio-lock-probe.c");
            var probePath = Path.Combine(root, "stdio-lock-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteStdioLockProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var ranges = new List<(long Offset, long Length)>();
            var rangesGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    lock (rangesGate)
                    {
                        ranges.Add((request.Offset, request.Length));
                    }

                    await using var target = new FileStream(
                        modelPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    target.Position = request.Offset;
                    await target.WriteAsync(content.AsMemory((int)request.Offset, (int)request.Length));
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 916,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "stdio-lock-session",
                    ["VFS_LOG"] = "0"
                });

            (long Offset, long Length)[] snapshot;
            lock (rangesGate)
            {
                snapshot = ranges.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(snapshot, Is.EqualTo(new[] { (0L, 1L), (1L, 1L) }));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_KernelCopySurfaces_GateManagedInputRanges()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "kernel-copy.bin");
            var content = "abcdefghijklmnop"u8.ToArray();
            await using (var placeholder = File.Create(modelPath))
            {
                placeholder.SetLength(content.Length);
            }

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "kernel-copy-probe.c");
            var probePath = Path.Combine(root, "kernel-copy-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteKernelCopyProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.Open)
                {
                    await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);
                }
                else if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await using var target = new FileStream(
                        modelPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    target.Position = request.Offset;
                    await target.WriteAsync(
                        content.AsMemory(
                            checked((int)request.Offset),
                            checked((int)request.Length)));
                }
                else if (request.Operation == VfsIpcOperation.EnsureComplete)
                {
                    await File.WriteAllBytesAsync(modelPath, content);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 411,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.EnsureRange)
                        .Select(request => request.Offset),
                    Is.EqualTo(new long[] { 0, 2, 4, 6, 8 }));
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.EnsureRange)
                        .All(request => request.Length == 2),
                    Is.True);
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureComplete),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(6));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_AlternateAndFortifiedReadSymbols_UseRangeProtocol()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "alternate-symbols.bin");
            var content = "abcdefghijklmnopqrstuvwxyz0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ+/"u8.ToArray();
            await using (var placeholder = File.Create(modelPath))
            {
                placeholder.SetLength(content.Length);
            }

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "alternate-symbols-probe.c");
            var probePath = Path.Combine(root, "alternate-symbols-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteAlternateReadProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                ["-O2", "-D_FORTIFY_SOURCE=2", probeSourcePath, "-o", probePath],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await using var target = new FileStream(
                        modelPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    target.Position = request.Offset;
                    await target.WriteAsync(
                        content.AsMemory(
                            checked((int)request.Offset),
                            checked((int)request.Length)));
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 301,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.EnsureRange)
                        .Select(request => request.Offset),
                    Is.EqualTo(new long[] { 2, 6, 10, 14, 18, 22, 26 }));
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.EnsureRange)
                        .All(request => request.Length == 2),
                    Is.True);
                Assert.That(snapshot.Any(request => request.Operation == VfsIpcOperation.Release), Is.True);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task RangeManaged_CloseWaitsForInFlightReadBeforeDescriptorReuse()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "close-race.bin");
            var replacementPath = Path.Combine(root, "replacement.bin");
            var ensureStartedPath = Path.Combine(root, "ensure-started");
            await File.WriteAllBytesAsync(modelPath, new byte[1]);
            await File.WriteAllTextAsync(replacementPath, "z");

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "close-race-probe.c");
            var probePath = Path.Combine(root, "close-race-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteCloseRaceProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await File.WriteAllTextAsync(ensureStartedPath, "ready");
                    await Task.Delay(300);
                    await File.WriteAllTextAsync(modelPath, "a");
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 923,
                        transferEpoch: 1,
                        expectedLength: 1),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath, replacementPath, ensureStartedPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "close-race-session",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task DuplicatedDescriptors_ConcurrentSequentialReads_EnsureDistinctOffsets()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "sequential.bin");
            var content = "abcdefgh"u8.ToArray();
            await using (var placeholder = File.Create(modelPath))
            {
                placeholder.SetLength(content.Length);
            }

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "sequential-probe.c");
            var probePath = Path.Combine(root, "sequential-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteSequentialProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var offsets = new List<long>();
            var offsetsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    lock (offsetsGate)
                    {
                        offsets.Add(request.Offset);
                    }

                    await Task.Delay(100);
                    await using var target = new FileStream(
                        modelPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    target.Position = request.Offset;
                    await target.WriteAsync(
                        content.AsMemory(
                            checked((int)request.Offset),
                            checked((int)request.Length)));
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 92,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            long[] observedOffsets;
            lock (offsetsGate)
            {
                observedOffsets = offsets.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("abcdefgh"));
                Assert.That(observedOffsets, Is.EqualTo(new long[] { 0, 4 }));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Dup2Replacement_WaitsForInFlightManagedReadBeforeReplacingDescriptor()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var sourcePath = Path.Combine(modelsRoot, "source.bin");
            var targetPath = Path.Combine(modelsRoot, "target.bin");
            var sourceContent = "source!!"u8.ToArray();
            var targetContent = "target!!"u8.ToArray();
            await File.WriteAllBytesAsync(sourcePath, new byte[sourceContent.Length]);
            await File.WriteAllBytesAsync(targetPath, new byte[targetContent.Length]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "dup2-replacement-probe.c");
            var probePath = Path.Combine(root, "dup2-replacement-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteDup2ReplacementProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                var isSource = request.LeaseId == 401 || string.Equals(request.Path, sourcePath, StringComparison.Ordinal);
                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    if (!isSource)
                    {
                        await Task.Delay(300);
                    }

                    await File.WriteAllBytesAsync(
                        isSource ? sourcePath : targetPath,
                        isSource ? sourceContent : targetContent);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        isSource ? sourcePath : targetPath,
                        leaseId: isSource ? 401UL : 402UL,
                        transferEpoch: 1,
                        expectedLength: sourceContent.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [sourcePath, targetPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task Freopen_PublishesManagedMappingBeforeConcurrentReadsProceed()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "freopen-atomic.bin");
            await File.WriteAllBytesAsync(modelPath, [0]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var blockerSourcePath = Path.Combine(root, "fcntl-blocker.c");
            var blockerPath = Path.Combine(root, "libfcntl_blocker.so");
            var probeSourcePath = Path.Combine(root, "freopen-atomic-probe.c");
            var probePath = Path.Combine(root, "freopen-atomic-probe");
            var markerPath = Path.Combine(root, "fcntl-blocked");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteFcntlBlockerAsync(blockerSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                ["-shared", "-fPIC", blockerSourcePath, "-o", blockerPath, "-ldl"],
                root);
            await WriteFreopenAtomicProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await File.WriteAllBytesAsync(modelPath, "a"u8.ToArray());
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 934,
                        transferEpoch: 1,
                        expectedLength: 1),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath, markerPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = $"{shimPath}:{blockerPath}",
                    ["FREOPEN_BLOCK_TARGET"] = modelPath,
                    ["FREOPEN_BLOCK_MARKER"] = markerPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_SESSION_ID"] = "freopen-atomic-session",
                    ["VFS_LOG"] = "0"
                });

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    requests.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.EqualTo(1));
                Assert.That(
                    requests.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task DescriptorAliasOpen_ProcAndDevFdRemainRangeManaged()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "descriptor-alias.bin");
            var content = "abcdefgh"u8.ToArray();
            await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "descriptor-alias-probe.c");
            var probePath = Path.Combine(root, "descriptor-alias-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteDescriptorAliasProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            ulong nextLeaseId = 700;
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await File.WriteAllBytesAsync(modelPath, content);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: nextLeaseId++,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Open),
                    Is.EqualTo(6));
                Assert.That(
                    snapshot.Where(request => request.Operation == VfsIpcOperation.Open)
                        .All(request => request.Path == modelPath),
                    Is.True);
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.EqualTo(5));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(6));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task CloseRange_DetachesClosedDescriptorsButPreservesCloseOnExecMappings()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "close-range.bin");
            var content = "abcdefgh"u8.ToArray();
            await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "close-range-probe.c");
            var probePath = Path.Combine(root, "close-range-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteCloseRangeProbeAsync(probeSourcePath);
            await RunProcessAsync(
                "/usr/bin/gcc",
                [probeSourcePath, "-o", probePath, "-pthread"],
                root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            ulong nextLeaseId = 500;
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await File.WriteAllBytesAsync(modelPath, content);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: nextLeaseId++,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Open),
                    Is.EqualTo(3));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.EqualTo(2));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(3));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task OrdinarySymlinkOutsideRoot_ResolvesToRangeManagedModel()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            var aliasesRoot = Path.Combine(root, "aliases");
            Directory.CreateDirectory(modelsRoot);
            Directory.CreateDirectory(aliasesRoot);
            var modelPath = Path.Combine(modelsRoot, "symlink-target.bin");
            var aliasPath = Path.Combine(aliasesRoot, "outside-root.bin");
            var content = "hydrated"u8.ToArray();
            await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);
            File.CreateSymbolicLink(aliasPath, modelPath);

            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "symlink-probe.c");
            var probePath = Path.Combine(root, "symlink-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteReadProbeAsync(probeSourcePath);
            await RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    await File.WriteAllBytesAsync(modelPath, content);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: 901,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [aliasPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout, Is.EqualTo("hydrated"));
                Assert.That(
                    snapshot.Single(request => request.Operation == VfsIpcOperation.Open).Path,
                    Is.EqualTo(modelPath));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.EqualTo(1));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.Release),
                    Is.EqualTo(1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task BulkCloseApis_ReconcileManagedDescriptorsBeforeReuse()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "bulk-close.bin");
            await File.WriteAllBytesAsync(modelPath, new byte[8]);
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "bulk-close-probe.c");
            var probePath = Path.Combine(root, "bulk-close-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteBulkCloseProbeAsync(probeSourcePath);
            await RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            ulong nextLeaseId = 910;
            await using var server = new VfsIpcServer(socketPath, (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                    return Task.FromResult(request.Operation switch
                    {
                        VfsIpcOperation.Open => RangeManagedResponse(
                            modelPath,
                            leaseId: nextLeaseId++,
                            transferEpoch: 1,
                            expectedLength: 8),
                        VfsIpcOperation.Release => VfsIpcResponse.Success(transferEpoch: 1),
                        _ => VfsIpcResponse.Error(VfsIpcStatus.ServerError)
                    });
                }
            });
            server.Start();

            await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Open), Is.EqualTo(2));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Release), Is.EqualTo(2));
                Assert.That(
                    snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureRange),
                    Is.InRange(0, 1));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Test]
    public async Task SparseExtentSeeks_WaitForCompleteHydration()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var modelPath = Path.Combine(modelsRoot, "extent-seek.bin");
            var content = "abcdefgh"u8.ToArray();
            await File.WriteAllBytesAsync(modelPath, new byte[content.Length]);
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "extent-seek-probe.c");
            var probePath = Path.Combine(root, "extent-seek-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await WriteExtentSeekProbeAsync(probeSourcePath);
            await RunProcessAsync("/usr/bin/gcc", [probeSourcePath, "-o", probePath], root);

            var requests = new List<VfsIpcRequest>();
            var requestsGate = new object();
            ulong nextLeaseId = 920;
            await using var server = new VfsIpcServer(socketPath, async (request, _) =>
            {
                lock (requestsGate)
                {
                    requests.Add(request);
                }

                if (request.Operation == VfsIpcOperation.EnsureComplete)
                {
                    await File.WriteAllBytesAsync(modelPath, content);
                }

                return request.Operation switch
                {
                    VfsIpcOperation.Open => RangeManagedResponse(
                        modelPath,
                        leaseId: nextLeaseId++,
                        transferEpoch: 1,
                        expectedLength: content.Length),
                    _ => VfsIpcResponse.Success(transferEpoch: 1)
                };
            });
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [modelPath],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            VfsIpcRequest[] snapshot;
            lock (requestsGate)
            {
                snapshot = requests.ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Open), Is.EqualTo(3));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.EnsureComplete), Is.EqualTo(3));
                Assert.That(snapshot.Count(request => request.Operation == VfsIpcOperation.Release), Is.EqualTo(3));
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static VfsIpcResponse RangeManagedResponse(
        string path,
        ulong leaseId,
        long transferEpoch,
        long expectedLength)
        => RangeManagedResponse(ReadIdentity(path), leaseId, transferEpoch, expectedLength);

    private static LinuxFileIdentity ReadIdentity(string path)
    {
        if (LinuxFileIdentityReader.TryRead(path, out var identity))
        {
            return identity;
        }

        throw new InvalidOperationException($"Could not read test file identity for '{path}'.");
    }

    private static VfsIpcResponse RangeManagedResponse(
        LinuxFileIdentity identity,
        ulong leaseId,
        long transferEpoch,
        long expectedLength)
        => new(
            VfsIpcStatus.Success,
            VfsIpcOpenDisposition.RangeManaged,
            leaseId,
            transferEpoch,
            expectedLength,
            identity.DeviceId,
            identity.Inode);

    [Test]
    public async Task Vfork_AndPosixSpawn_RunChildrenFromPreloadedProcess()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");

        var root = Path.Combine(Path.GetTempPath(), "runneragent-vfs-shim-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        try
        {
            var modelsRoot = Path.Combine(root, "models");
            Directory.CreateDirectory(modelsRoot);
            var shimPath = Path.Combine(root, "liboffloadr_model_vfs.so");
            var probeSourcePath = Path.Combine(root, "spawn-probe.c");
            var probePath = Path.Combine(root, "spawn-probe");
            var socketPath = Path.Combine(root, "model-fetch.sock");

            await CompileShimAsync(shimPath);
            await File.WriteAllTextAsync(probeSourcePath, """
                #define _GNU_SOURCE
                #include <spawn.h>
                #include <stdio.h>
                #include <sys/wait.h>
                #include <unistd.h>

                extern char **environ;

                /* Not inlined, so the vfork caller has a frame of its own to lose. */
                __attribute__((noinline)) static int run_with_vfork(int round) {
                    volatile unsigned long canary = 0x5a5a5a5aUL + (unsigned long)round;
                    char *arguments[] = {"/bin/sh", "-c", "exit 7", NULL};
                    pid_t child = vfork();
                    if (child == 0) {
                        execve("/bin/sh", arguments, environ);
                        _exit(127);
                    }
                    if (child < 0) return 20;
                    int status = 0;
                    if (waitpid(child, &status, 0) != child) return 21;
                    if (!WIFEXITED(status) || WEXITSTATUS(status) != 7) return 22;
                    if (canary != 0x5a5a5a5aUL + (unsigned long)round) return 23;
                    return 0;
                }

                int main(void) {
                    for (int round = 0; round < 200; round++) {
                        int result = run_with_vfork(round);
                        if (result != 0) return result;
                    }

                    pid_t child = -1;
                    char *arguments[] = {"/bin/sh", "-c", "exit 5", NULL};
                    if (posix_spawn(&child, "/bin/sh", NULL, NULL, arguments, environ) != 0) return 30;
                    int status = 0;
                    if (waitpid(child, &status, 0) != child) return 31;
                    if (!WIFEXITED(status) || WEXITSTATUS(status) != 5) return 32;

                    puts("ok");
                    return 0;
                }
                """);
            await RunProcessAsync("/usr/bin/gcc", ["-O2", probeSourcePath, "-o", probePath], root);

            await using var server = new VfsIpcServer(
                socketPath,
                (_, _) => Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.NotManaged)));
            server.Start();

            var result = await RunProcessAsync(
                probePath,
                [],
                root,
                new Dictionary<string, string?>
                {
                    ["LD_PRELOAD"] = shimPath,
                    ["VFS_SOCKET"] = socketPath,
                    ["VFS_ROOTS"] = modelsRoot,
                    ["VFS_EXTS"] = ".bin",
                    ["VFS_LOG"] = "0"
                });

            Assert.That(result.Stdout.Trim(), Is.EqualTo("ok"));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    internal static async Task CompileShimAsync(string outputPath)
    {
        var sourcePath = FindRepoFile("src/Offloadr.Runner.Linux/VfsShim/model_vfs.c");
        await RunProcessAsync(
            "/usr/bin/gcc",
            ["-fPIC", "-shared", "-O2", "-o", outputPath, sourcePath, "-ldl", "-pthread"],
            Path.GetDirectoryName(outputPath)!);
    }

    private static async Task WriteDescriptorAliasProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <pthread.h>
            #include <stdio.h>
            #include <string.h>
            #include <sys/syscall.h>
            #include <unistd.h>

            static int read_alias(const char *alias, const char *expected) {
                int fd = open(alias, O_RDONLY);
                if (fd < 0) {
                    perror("alias open");
                    return 1;
                }
                char bytes[4] = {0};
                ssize_t count = read(fd, bytes, sizeof(bytes));
                int close_result = close(fd);
                if (count != 4) {
                    perror("alias read");
                    fprintf(stderr, "alias read count=%zd\n", count);
                    return 2;
                }
                if (memcmp(bytes, expected, 4) != 0) {
                    fprintf(stderr, "alias bytes=%02x%02x%02x%02x\n",
                        (unsigned char)bytes[0],
                        (unsigned char)bytes[1],
                        (unsigned char)bytes[2],
                        (unsigned char)bytes[3]);
                    return 3;
                }
                if (close_result != 0) {
                    perror("alias close");
                    return 4;
                }
                return 0;
            }

            struct thread_arguments {
                int source;
                int result;
            };

            static void *read_thread_aliases(void *opaque) {
                struct thread_arguments *arguments = opaque;
                long process_id = (long)getpid();
                long thread_id = (long)syscall(SYS_gettid);
                char thread_self_alias[64];
                char thread_id_alias[64];
                char task_alias[96];
                snprintf(
                    thread_self_alias,
                    sizeof(thread_self_alias),
                    "/proc/thread-self/fd/%d",
                    arguments->source);
                snprintf(
                    thread_id_alias,
                    sizeof(thread_id_alias),
                    "/proc/%ld/fd/%d",
                    thread_id,
                    arguments->source);
                snprintf(
                    task_alias,
                    sizeof(task_alias),
                    "/proc/%ld/task/%ld/fd/%d",
                    process_id,
                    thread_id,
                    arguments->source);

                int result = read_alias(thread_self_alias, "abcd");
                if (result == 0) result = read_alias(thread_id_alias, "abcd");
                if (result == 0) result = read_alias(task_alias, "abcd");
                arguments->result = result;
                return NULL;
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                int source = open(argv[1], O_RDONLY);
                if (source < 0) return 11;

                char proc_alias[64];
                char dev_alias[64];
                snprintf(proc_alias, sizeof(proc_alias), "/proc/self/fd/%d", source);
                snprintf(dev_alias, sizeof(dev_alias), "/dev/fd/%d", source);
                int proc_result = read_alias(proc_alias, "abcd");
                if (proc_result != 0) return 20 + proc_result;
                int dev_result = read_alias(dev_alias, "abcd");
                if (dev_result != 0) return 30 + dev_result;
                struct thread_arguments arguments = {
                    .source = source,
                    .result = 0
                };
                pthread_t thread;
                if (pthread_create(&thread, NULL, read_thread_aliases, &arguments) != 0) return 40;
                if (pthread_join(thread, NULL) != 0 || arguments.result != 0) {
                    return 41 + arguments.result;
                }
                if (close(source) != 0) return 14;

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteWritableProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <stdio.h>
            #include <string.h>
            #include <sys/stat.h>
            #include <unistd.h>

            static int check_temporary_mode(int fd, mode_t expected) {
                struct stat info;
                if (fd < 0 || fstat(fd, &info) != 0) return 1;
                return (info.st_mode & 0777) != expected;
            }

            int main(int argc, char **argv) {
                if (argc != 4) return 10;

                int fd = open(argv[1], O_WRONLY | O_CREAT | O_TRUNC, 0600);
                if (fd < 0) return 11;
                if (write(fd, "open", 4) != 4) return 12;
                if (close(fd) != 0) return 13;

                FILE *stream = fopen(argv[2], "wb");
                if (!stream) return 14;
                if (fwrite("fopen", 1, 5, stream) != 5) return 15;
                if (fclose(stream) != 0) return 16;

                int temporary = open(argv[3], O_TMPFILE | O_RDWR, 0600);
                if (check_temporary_mode(temporary, 0600)) return 17;
                close(temporary);
                temporary = open64(argv[3], O_TMPFILE | O_RDWR, 0600);
                if (check_temporary_mode(temporary, 0600)) return 18;
                close(temporary);

                int directory = open(argv[3], O_RDONLY | O_DIRECTORY);
                if (directory < 0) return 19;
                temporary = openat(directory, ".", O_TMPFILE | O_RDWR, 0600);
                if (check_temporary_mode(temporary, 0600)) return 20;
                close(temporary);
                temporary = openat64(directory, ".", O_TMPFILE | O_RDWR, 0600);
                if (check_temporary_mode(temporary, 0600)) return 21;
                close(temporary);
                close(directory);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteFortifiedOpenProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #define _LARGEFILE64_SOURCE
            #define _FORTIFY_SOURCE 2
            #include <fcntl.h>
            #include <stdio.h>
            #include <unistd.h>

            static int runtime_read_flags(void) {
                volatile int flags = O_RDONLY;
                return flags;
            }

            int main(int argc, char **argv) {
                if (argc != 4) return 10;
                int fd = open(argv[3], runtime_read_flags());
                if (fd < 0) return 11;
                close(fd);

                fd = open64(argv[3], runtime_read_flags());
                if (fd < 0) return 12;
                close(fd);

                int directory = open(argv[1], O_RDONLY | O_DIRECTORY);
                if (directory < 0) return 13;
                fd = openat(directory, argv[2], runtime_read_flags());
                if (fd < 0) return 14;
                close(fd);

                fd = openat64(directory, argv[2], runtime_read_flags());
                if (fd < 0) return 15;
                close(fd);
                close(directory);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteSurfaceProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #define _LARGEFILE64_SOURCE
            #define _FILE_OFFSET_BITS 64
            #include <errno.h>
            #include <fcntl.h>
            #include <stdarg.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <sys/mman.h>
            #include <sys/stat.h>
            #include <sys/uio.h>
            #include <unistd.h>
            #include <wchar.h>

            static int must_open_close(const char *path) {
                int fd = open(path, O_RDONLY);
                if (fd < 0) return 1;
                return close(fd) != 0;
            }

            static int scan_three(FILE *file, char *buffer, const char *format, ...) {
                va_list arguments;
                va_start(arguments, format);
                int result = vfscanf(file, format, arguments);
                va_end(arguments);
                return result;
            }

            int main(int argc, char **argv) {
                if (argc != 3) return 10;
                const char *path = argv[1];
                const char *lexical_path = argv[2];
                int directory = open(".", O_RDONLY | O_DIRECTORY);
                if (directory < 0) return 11;

                if (must_open_close(path)) return 12;
                int temporary = openat(AT_FDCWD, path, O_RDONLY);
                if (temporary < 0) return 13;
                close(temporary);
                temporary = openat64(AT_FDCWD, path, O_RDONLY);
                if (temporary < 0) return 14;
                close(temporary);

                temporary = open(lexical_path, O_RDONLY);
                if (temporary < 0) return 54;
                char lexical_byte = 0;
                if (read(temporary, &lexical_byte, 1) != 1 || lexical_byte != 'a') return 55;
                close(temporary);

                FILE *plain = fopen(path, "rb");
                if (!plain) return 15;
                if (fgetc(plain) != 'a') return 35;
                fclose(plain);
                plain = fopen64(path, "rb");
                if (!plain) return 16;
                char line[4];
                if (!fgets(line, sizeof(line), plain) || memcmp(line, "abc", 3) != 0) return 36;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 40;
                if (getc_unlocked(plain) != 'a') return 41;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 42;
                if (!fgets_unlocked(line, sizeof(line), plain) || memcmp(line, "abc", 3) != 0) return 43;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 37;
                char *dynamic_line = NULL;
                size_t dynamic_capacity = 0;
                if (getline(&dynamic_line, &dynamic_capacity, plain) != 32) return 38;
                free(dynamic_line);
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 44;
                memset(line, 0, sizeof(line));
                if (fscanf(plain, "%3c", line) != 1 || memcmp(line, "abc", 3) != 0) return 45;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 46;
                memset(line, 0, sizeof(line));
                if (scan_three(plain, line, "%3c") != 1 || memcmp(line, "abc", 3) != 0) return 47;
                fclose(plain);

                plain = tmpfile();
                if (!plain || !freopen(path, "rb", plain)) return 48;
                if (fgetc(plain) != 'a') return 49;
                fclose(plain);

                plain = tmpfile();
                if (!plain || !freopen64(path, "rb", plain)) return 50;
                if (fgetc(plain) != 'a') return 51;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 56;
                if (fgetwc(plain) != L'a') return 57;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 58;
                if (getwc_unlocked(plain) != L'a') return 59;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 60;
                wchar_t wide_line[4] = {0};
                if (!fgetws(wide_line, 4, plain) || wmemcmp(wide_line, L"abc", 3) != 0) return 61;
                fclose(plain);

                plain = fopen(path, "rb");
                if (!plain) return 62;
                memset(wide_line, 0, sizeof(wide_line));
                if (fwscanf(plain, L"%3lc", wide_line) != 1 ||
                    wmemcmp(wide_line, L"abc", 3) != 0) return 63;
                fclose(plain);

                int fd = open64(path, O_RDONLY);
                if (fd < 0) return 17;
                struct stat info;
                if (fstat(fd, &info) != 0 || info.st_size != 32) return 18;

                int duplicate = dup(fd);
                int duplicate2 = dup2(fd, 100);
                int duplicate3 = dup3(fd, 101, O_CLOEXEC);
                int duplicate4 = fcntl(fd, F_DUPFD_CLOEXEC, 102);
                if (duplicate < 0 || duplicate2 < 0 || duplicate3 < 0 || duplicate4 < 0) return 19;

                int invalid = dup(fd);
                if (invalid < 0 || close(invalid) != 0) return 64;
                errno = 0;
                if (dup2(invalid, invalid) != -1 || errno != EBADF) return 65;

                char scalar[4];
                if (pread(duplicate4, scalar, 2, 0) != 2 || memcmp(scalar, "ab", 2) != 0) return 39;
                if (pread(fd, scalar, 2, 2) != 2) return 20;
                if (pread64(fd, scalar, 2, 4) != 2) return 21;
                struct iovec positional[2] = {
                    { .iov_base = scalar, .iov_len = 1 },
                    { .iov_base = scalar + 1, .iov_len = 1 }
                };
                if (preadv(fd, positional, 2, 6) != 2) return 22;
                errno = 0;
                if (preadv2(fd, positional, 2, 8, 0) < 0 && errno != ENOSYS) return 23;

                if (lseek64(fd, 0, SEEK_SET) != 0) return 24;
                if (read(fd, scalar, 2) != 2) return 25;
                struct iovec sequential[2] = {
                    { .iov_base = scalar, .iov_len = 1 },
                    { .iov_base = scalar + 1, .iov_len = 1 }
                };
                if (readv(fd, sequential, 2) != 2) return 26;

                if (lseek(duplicate, 0, SEEK_SET) != 0) return 27;
                FILE *stream = fdopen(duplicate, "rb");
                if (!stream) return 28;
                if (fread(scalar, 1, 2, stream) != 2) return 29;
                fclose(stream);

                if (lseek(duplicate2, 0, SEEK_SET) != 0) return 30;
                stream = fdopen(duplicate2, "rb");
                if (!stream) return 31;
                if (fread_unlocked(scalar, 1, 2, stream) != 2) return 32;
                fclose(stream);

                void *mapping = mmap64(NULL, 32, PROT_READ, MAP_PRIVATE, fd, 0);
                if (mapping == MAP_FAILED || ((char *)mapping)[0] != 'a') return 33;
                munmap(mapping, 32);

                close(duplicate3);
                close(duplicate4);
                close(fd);
                close(directory);

                errno = 0;
                if (open(path, O_WRONLY | O_TRUNC) >= 0 || errno != EROFS) return 34;
                errno = 0;
                if (creat(path, 0600) >= 0 || errno != EROFS) return 52;
                errno = 0;
                if (creat64(path, 0600) >= 0 || errno != EROFS) return 53;

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteOptimizedUnlockedAndAioProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #define _LARGEFILE64_SOURCE
            #include <aio.h>
            #include <errno.h>
            #include <fcntl.h>
            #include <stdio.h>
            #include <string.h>
            #include <unistd.h>

            static int wait_for_aio(struct aiocb *control, const char *expected) {
                int error;
                while ((error = aio_error(control)) == EINPROGRESS) usleep(1000);
                return error != 0 || aio_return(control) != 2 ||
                       memcmp((const void *)control->aio_buf, expected, 2) != 0;
            }

            static int wait_for_aio64(struct aiocb64 *control, const char *expected) {
                int error;
                while ((error = aio_error64(control)) == EINPROGRESS) usleep(1000);
                return error != 0 || aio_return64(control) != 2 ||
                       memcmp((const void *)control->aio_buf, expected, 2) != 0;
            }

            static int read_after_buffer_configuration(const char *path, int variant) {
                FILE *stream = fopen(path, "rb");
                if (!stream) return 1;
                char stream_buffer[16] = {0};
                if (variant == 0) {
                    if (setvbuf(stream, stream_buffer, _IOFBF, sizeof(stream_buffer)) != 0) return 2;
                }
                else if (variant == 1) {
                    setbuf(stream, stream_buffer);
                }
                else if (variant == 2) {
                    setbuffer(stream, stream_buffer, sizeof(stream_buffer));
                }
                else {
                    setlinebuf(stream);
                }

                char output[2] = {0};
                if (fread_unlocked(output, 1, sizeof(output), stream) != sizeof(output) ||
                    memcmp(output, "ab", sizeof(output)) != 0) return 3;
                return fclose(stream) == 0 ? 0 : 4;
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                char buffer[3] = {0};

                for (int variant = 0; variant < 4; variant++) {
                    int result = read_after_buffer_configuration(argv[1], variant);
                    if (result != 0) return 11 + (variant * 4) + result;
                }

                FILE *word_stream = fopen(argv[1], "rb");
                if (!word_stream) return 28;
                int word = getw(word_stream);
                if (memcmp(&word, "abcd", sizeof(word)) != 0) return 29;
                fclose(word_stream);

                int fd = open(argv[1], O_RDONLY);
                if (fd < 0) return 13;
                memset(buffer, 0, sizeof(buffer));
                struct aiocb read_request = {0};
                read_request.aio_fildes = fd;
                read_request.aio_buf = buffer;
                read_request.aio_nbytes = 2;
                read_request.aio_offset = 2;
                if (aio_read(&read_request) != 0 || wait_for_aio(&read_request, "cd")) return 14;
                close(fd);

                fd = open(argv[1], O_RDONLY);
                if (fd < 0) return 15;
                memset(buffer, 0, sizeof(buffer));
                struct aiocb64 read_request64 = {0};
                read_request64.aio_fildes = fd;
                read_request64.aio_buf = buffer;
                read_request64.aio_nbytes = 2;
                read_request64.aio_offset = 4;
                if (aio_read64(&read_request64) != 0 || wait_for_aio64(&read_request64, "ef")) return 16;
                close(fd);

                fd = open(argv[1], O_RDONLY);
                if (fd < 0) return 17;
                memset(buffer, 0, sizeof(buffer));
                struct aiocb list_request = {0};
                list_request.aio_fildes = fd;
                list_request.aio_lio_opcode = LIO_READ;
                list_request.aio_buf = buffer;
                list_request.aio_nbytes = 2;
                list_request.aio_offset = 6;
                struct aiocb *list[] = {&list_request};
                if (lio_listio(LIO_WAIT, list, 1, NULL) != 0 || wait_for_aio(&list_request, "gh")) return 18;
                close(fd);

                fd = open(argv[1], O_RDONLY);
                if (fd < 0) return 19;
                memset(buffer, 0, sizeof(buffer));
                struct aiocb64 list_request64 = {0};
                list_request64.aio_fildes = fd;
                list_request64.aio_lio_opcode = LIO_READ;
                list_request64.aio_buf = buffer;
                list_request64.aio_nbytes = 2;
                list_request64.aio_offset = 8;
                struct aiocb64 *list64[] = {&list_request64};
                if (lio_listio64(LIO_WAIT, list64, 1, NULL) != 0 ||
                    wait_for_aio64(&list_request64, "ij")) return 20;
                close(fd);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteInheritanceProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <sched.h>
            #include <signal.h>
            #include <spawn.h>
            #include <stdio.h>
            #include <sys/ioctl.h>
            #include <sys/wait.h>
            #include <unistd.h>

            extern char **environ;

            static int clone_child(void *argument) {
                (void)argument;
                return 0;
            }

            static int is_close_on_exec(int fd) {
                int flags = fcntl(fd, F_GETFD);
                return flags >= 0 && (flags & FD_CLOEXEC) != 0;
            }

            int main(int argc, char **argv) {
                if (argc != 4) return 10;
                char clone_stack[64 * 1024];
                pid_t clone_pid = clone(
                    clone_child,
                    clone_stack + sizeof(clone_stack),
                    SIGCHLD,
                    NULL);
                int clone_status = 0;
                if (clone_pid < 0 ||
                    waitpid(clone_pid, &clone_status, 0) != clone_pid ||
                    !WIFEXITED(clone_status) || WEXITSTATUS(clone_status) != 0) return 50;
                int managed = open(argv[1], O_RDONLY);
                if (managed < 0 || !is_close_on_exec(managed)) return 11;
                if (fcntl(managed, F_SETFD, 0) != 0 || !is_close_on_exec(managed)) return 12;
                errno = 0;
                if (ioctl(managed, FIONCLEX) != -1 || errno != EOPNOTSUPP ||
                    !is_close_on_exec(managed)) return 13;

                int duplicate = dup(managed);
                if (duplicate < 0 || !is_close_on_exec(duplicate)) return 14;
                int target = open("/dev/null", O_RDONLY);
                if (target < 0) return 15;
                errno = 0;
                if (dup2(managed, target) != -1 || errno != EOPNOTSUPP) return 15;
                close(target);
                if (dup2(managed, target) != target || !is_close_on_exec(target)) return 15;
                int fcntl_duplicate = fcntl(managed, F_DUPFD, target + 1);
                if (fcntl_duplicate < 0 || !is_close_on_exec(fcntl_duplicate)) return 16;

                posix_spawn_file_actions_t immediate_actions;
                if (posix_spawn_file_actions_init(&immediate_actions) != 0) return 17;
                if (posix_spawn_file_actions_adddup2(
                        &immediate_actions,
                        managed,
                        STDIN_FILENO) != EOPNOTSUPP) return 18;
                if (posix_spawn_file_actions_destroy(&immediate_actions) != 0) return 19;

                posix_spawn_file_actions_t deferred_actions;
                if (posix_spawn_file_actions_init(&deferred_actions) != 0) return 20;
                int deferred_source = open("/dev/null", O_RDONLY);
                if (deferred_source < 0 || posix_spawn_file_actions_adddup2(
                        &deferred_actions,
                        deferred_source,
                        STDIN_FILENO) != 0) return 21;
                close(deferred_source);
                if (dup2(managed, deferred_source) != deferred_source) return 22;
                pid_t child = -1;
                char *spawn_arguments[] = {"true", NULL};
                if (posix_spawn(
                        &child,
                        "/bin/true",
                        &deferred_actions,
                        NULL,
                        spawn_arguments,
                        environ) != EOPNOTSUPP) return 23;
                if (posix_spawnp(
                        &child,
                        "true",
                        &deferred_actions,
                        NULL,
                        spawn_arguments,
                        environ) != EOPNOTSUPP) return 24;
                close(deferred_source);
                if (posix_spawn_file_actions_destroy(&deferred_actions) != 0) return 25;

                posix_spawn_file_actions_t immediate_open_actions;
                if (posix_spawn_file_actions_init(&immediate_open_actions) != 0) return 26;
                if (posix_spawn_file_actions_addopen(
                        &immediate_open_actions,
                        STDIN_FILENO,
                        argv[1],
                        O_RDONLY,
                        0) != EOPNOTSUPP) return 27;
                if (posix_spawn_file_actions_destroy(&immediate_open_actions) != 0) return 28;

                unlink(argv[2]);
                if (symlink("/dev/null", argv[2]) != 0) return 29;
                posix_spawn_file_actions_t deferred_open_actions;
                if (posix_spawn_file_actions_init(&deferred_open_actions) != 0) return 30;
                if (posix_spawn_file_actions_addopen(
                        &deferred_open_actions,
                        STDIN_FILENO,
                        argv[2],
                        O_RDONLY,
                        0) != 0) return 31;
                if (unlink(argv[2]) != 0 || symlink(argv[1], argv[2]) != 0) return 32;
                if (posix_spawn(
                        &child,
                        "/bin/true",
                        &deferred_open_actions,
                        NULL,
                        spawn_arguments,
                        environ) != EOPNOTSUPP) return 33;
                if (posix_spawnp(
                        &child,
                        "true",
                        &deferred_open_actions,
                        NULL,
                        spawn_arguments,
                        environ) != EOPNOTSUPP) return 34;
                if (posix_spawn_file_actions_destroy(&deferred_open_actions) != 0) return 35;

                posix_spawn_file_actions_t chdir_open_actions;
                if (posix_spawn_file_actions_init(&chdir_open_actions) != 0) return 36;
                if (posix_spawn_file_actions_addchdir_np(
                        &chdir_open_actions,
                        argv[3]) != 0) return 37;
                if (posix_spawn_file_actions_addopen(
                        &chdir_open_actions,
                        STDIN_FILENO,
                        "inheritance.bin",
                        O_RDONLY,
                        0) != EOPNOTSUPP) return 38;
                if (posix_spawn_file_actions_destroy(&chdir_open_actions) != 0) return 39;

                int models_directory = open(argv[3], O_RDONLY | O_DIRECTORY);
                if (models_directory < 0) return 40;
                posix_spawn_file_actions_t fchdir_open_actions;
                if (posix_spawn_file_actions_init(&fchdir_open_actions) != 0) return 41;
                if (posix_spawn_file_actions_addfchdir_np(
                        &fchdir_open_actions,
                        models_directory) != 0) return 42;
                if (posix_spawn_file_actions_addopen(
                        &fchdir_open_actions,
                        STDIN_FILENO,
                        "inheritance.bin",
                        O_RDONLY,
                        0) != EOPNOTSUPP) return 43;
                if (posix_spawn_file_actions_destroy(&fchdir_open_actions) != 0) return 44;
                close(models_directory);

                if (posix_spawn(
                        &child,
                        "/bin/true",
                        NULL,
                        NULL,
                        spawn_arguments,
                        environ) != 0) return 45;
                int child_status = 0;
                if (waitpid(child, &child_status, 0) != child ||
                    !WIFEXITED(child_status) || WEXITSTATUS(child_status) != 0) return 46;

                errno = 0;
                if (fork() != -1 || errno != EOPNOTSUPP) return 47;
                errno = 0;
                if (vfork() != -1 || errno != EOPNOTSUPP) return 48;
                errno = 0;
                if (clone(
                        clone_child,
                        clone_stack + sizeof(clone_stack),
                        SIGCHLD,
                        NULL) != -1 || errno != EOPNOTSUPP) return 49;

                close(fcntl_duplicate);
                close(target);
                close(duplicate);
                close(managed);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteDescriptorMessageProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <stdio.h>
            #include <string.h>
            #include <sys/socket.h>
            #include <unistd.h>

            static void prepare_message(
                struct msghdr *message,
                struct iovec *payload,
                char *control,
                size_t control_length,
                int descriptor) {
                memset(message, 0, sizeof(*message));
                memset(control, 0, control_length);
                static char byte = 'x';
                payload->iov_base = &byte;
                payload->iov_len = sizeof(byte);
                message->msg_iov = payload;
                message->msg_iovlen = 1;
                message->msg_control = control;
                message->msg_controllen = control_length;
                struct cmsghdr *header = CMSG_FIRSTHDR(message);
                header->cmsg_level = SOL_SOCKET;
                header->cmsg_type = SCM_RIGHTS;
                header->cmsg_len = CMSG_LEN(sizeof(descriptor));
                memcpy(CMSG_DATA(header), &descriptor, sizeof(descriptor));
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                int sockets[2];
                if (socketpair(AF_UNIX, SOCK_DGRAM | SOCK_CLOEXEC, 0, sockets) != 0) return 11;
                int managed = open(argv[1], O_RDONLY);
                if (managed < 0) return 12;

                char control[CMSG_SPACE(sizeof(int))];
                struct iovec payload;
                struct msghdr message;
                prepare_message(&message, &payload, control, sizeof(control), managed);
                errno = 0;
                if (sendmsg(sockets[0], &message, 0) != -1 || errno != EOPNOTSUPP) return 13;

                struct mmsghdr batch = {0};
                prepare_message(&batch.msg_hdr, &payload, control, sizeof(control), managed);
                errno = 0;
                if (sendmmsg(sockets[0], &batch, 1, 0) != -1 || errno != EOPNOTSUPP) return 14;

                close(managed);
                close(sockets[0]);
                close(sockets[1]);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteBufferedDupProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <stdio.h>
            #include <unistd.h>

            static int replacement_is_rejected(int managed, const char *ordinary_path, int use_dup3) {
                FILE *stream = fopen(ordinary_path, "r");
                if (!stream || fgetc(stream) != 'o') return 0;
                int target = fileno(stream);
                errno = 0;
                int result = use_dup3
                    ? dup3(managed, target, O_CLOEXEC)
                    : dup2(managed, target);
                if (result != -1 || errno != EOPNOTSUPP) return 0;
                int unchanged = fgetc(stream) == 'r';
                fclose(stream);
                return unchanged;
            }

            int main(int argc, char **argv) {
                if (argc != 3) return 10;
                int managed = open(argv[1], O_RDONLY);
                if (managed < 0) return 11;
                if (!replacement_is_rejected(managed, argv[2], 0)) return 12;
                if (!replacement_is_rejected(managed, argv[2], 1)) return 13;
                close(managed);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteStdioLockProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <pthread.h>
            #include <signal.h>
            #include <stdio.h>
            #include <unistd.h>

            struct read_args {
                FILE *stream;
                char byte;
                size_t result;
            };

            static void *read_one(void *raw) {
                struct read_args *args = raw;
                args->result = fread(&args->byte, 1, 1, args->stream);
                return NULL;
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                alarm(3);
                FILE *stream = fopen(argv[1], "rb");
                if (!stream) return 11;

                flockfile(stream);
                struct read_args args = { .stream = stream };
                pthread_t reader;
                if (pthread_create(&reader, NULL, read_one, &args) != 0) return 12;
                usleep(200000);

                int first = fgetc(stream);
                funlockfile(stream);
                if (first != 'a') return 13;
                if (pthread_join(reader, NULL) != 0) return 14;
                if (args.result != 1 || args.byte != 'b') return 15;

                fclose(stream);
                alarm(0);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteReleaseRetryProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #include <fcntl.h>
            #include <stdio.h>
            #include <unistd.h>

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                int fd = open(argv[1], O_RDONLY);
                if (fd < 0) return 11;
                if (close(fd) != 0) return 12;
                usleep(400000);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteKernelCopyProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #define _LARGEFILE64_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <linux/fs.h>
            #include <stdio.h>
            #include <string.h>
            #include <sys/ioctl.h>
            #include <sys/sendfile.h>
            #include <unistd.h>

            static int check_output(int output_fd, const char *expected) {
                char actual[2] = {0};
                return pread(output_fd, actual, sizeof(actual), 0) != 2 ||
                       memcmp(actual, expected, sizeof(actual)) != 0;
            }

            static int open_source(const char *path) {
                return open(path, O_RDONLY);
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;

                int source = open_source(argv[1]);
                FILE *output = tmpfile();
                off_t offset = 0;
                if (source < 0 || !output ||
                    sendfile(fileno(output), source, &offset, 2) != 2 ||
                    check_output(fileno(output), "ab")) return 11;
                fclose(output);
                close(source);

                source = open_source(argv[1]);
                output = tmpfile();
                off64_t offset64 = 2;
                if (source < 0 || !output ||
                    sendfile64(fileno(output), source, &offset64, 2) != 2 ||
                    check_output(fileno(output), "cd")) return 12;
                fclose(output);
                close(source);

                source = open_source(argv[1]);
                output = tmpfile();
                off64_t input_offset = 4;
                off64_t output_offset = 0;
                if (source < 0 || !output ||
                    copy_file_range(
                        source,
                        &input_offset,
                        fileno(output),
                        &output_offset,
                        2,
                        0) != 2 ||
                    check_output(fileno(output), "ef")) return 13;
                fclose(output);
                close(source);

                source = open_source(argv[1]);
                int pipe_fds[2];
                input_offset = 6;
                char spliced[2] = {0};
                if (source < 0 || pipe(pipe_fds) != 0 ||
                    splice(source, &input_offset, pipe_fds[1], NULL, 2, 0) != 2 ||
                    read(pipe_fds[0], spliced, sizeof(spliced)) != 2 ||
                    memcmp(spliced, "gh", sizeof(spliced)) != 0) return 14;
                close(pipe_fds[0]);
                close(pipe_fds[1]);
                close(source);

                source = open_source(argv[1]);
                output = tmpfile();
                struct file_clone_range clone_range = {
                    .src_fd = source,
                    .src_offset = 8,
                    .src_length = 2,
                    .dest_offset = 0
                };
                errno = 0;
                int clone_result = source < 0 || !output
                    ? -1
                    : ioctl(fileno(output), FICLONERANGE, &clone_range);
                if (clone_result != 0 && errno != EOPNOTSUPP && errno != EXDEV &&
                    errno != EINVAL && errno != ENOTTY) return 15;
                if (clone_result == 0 && check_output(fileno(output), "ij")) return 16;
                if (output) fclose(output);
                if (source >= 0) close(source);

                source = open_source(argv[1]);
                output = tmpfile();
                errno = 0;
                clone_result = source < 0 || !output
                    ? -1
                    : ioctl(fileno(output), FICLONE, source);
                if (clone_result != 0 && errno != EOPNOTSUPP && errno != EXDEV &&
                    errno != EINVAL && errno != ENOTTY) return 17;
                if (clone_result == 0) {
                    char cloned[16] = {0};
                    if (pread(fileno(output), cloned, sizeof(cloned), 0) != 16 ||
                        memcmp(cloned, "abcdefghijklmnop", sizeof(cloned)) != 0) return 18;
                }
                if (output) fclose(output);
                if (source >= 0) close(source);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteAlternateReadProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #define _LARGEFILE64_SOURCE
            #include <fcntl.h>
            #include <stdio.h>
            #include <string.h>
            #include <sys/uio.h>
            #include <unistd.h>

            extern ssize_t __read_chk(int, void *, size_t, size_t);
            extern ssize_t __pread_chk(int, void *, size_t, off_t, size_t);
            extern ssize_t __pread64_chk(int, void *, size_t, off64_t, size_t);
            extern size_t __fread_chk(void *, size_t, size_t, size_t, FILE *);
            extern size_t __fread_unlocked_chk(void *, size_t, size_t, size_t, FILE *);
            extern ssize_t preadv64v2(int, const struct iovec *, int, off64_t, int);

            static int matches(const char *actual, const char *expected) {
                return actual[0] == expected[0] && actual[1] == expected[1];
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                int fd = open64(argv[1], O_RDONLY);
                if (fd < 0) return 11;
                char buffer[2] = {0};
                struct iovec vectors[2] = {
                    { .iov_base = buffer, .iov_len = 1 },
                    { .iov_base = buffer + 1, .iov_len = 1 }
                };

                if (preadv64(fd, vectors, 2, 2) != 2 || !matches(buffer, "cd")) return 12;
                memset(buffer, 0, sizeof(buffer));
                if (preadv64v2(fd, vectors, 2, 6, 0) != 2 || !matches(buffer, "gh")) return 13;

                memset(buffer, 0, sizeof(buffer));
                if (lseek(fd, 10, SEEK_SET) != 10) return 14;
                if (__read_chk(fd, buffer, 2, sizeof(buffer)) != 2 || !matches(buffer, "kl")) return 15;
                memset(buffer, 0, sizeof(buffer));
                if (__pread_chk(fd, buffer, 2, 14, sizeof(buffer)) != 2 || !matches(buffer, "op")) return 16;
                memset(buffer, 0, sizeof(buffer));
                if (__pread64_chk(fd, buffer, 2, 18, sizeof(buffer)) != 2 || !matches(buffer, "st")) return 17;

                int stream_fd = dup(fd);
                if (stream_fd < 0) return 18;
                FILE *stream = fdopen(stream_fd, "rb");
                if (!stream) return 19;
                memset(buffer, 0, sizeof(buffer));
                if (lseek(stream_fd, 22, SEEK_SET) != 22) return 20;
                if (__fread_chk(buffer, sizeof(buffer), 1, 2, stream) != 2 || !matches(buffer, "wx")) return 21;
                memset(buffer, 0, sizeof(buffer));
                if (lseek(stream_fd, 26, SEEK_SET) != 26) return 22;
                if (__fread_unlocked_chk(buffer, sizeof(buffer), 1, 2, stream) != 2 || !matches(buffer, "01")) return 23;

                fclose(stream);
                close(fd);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteSequentialProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <pthread.h>
            #include <stdio.h>
            #include <unistd.h>

            struct read_args {
                int fd;
                pthread_barrier_t *barrier;
                char bytes[4];
            };

            static void *read_four(void *raw) {
                struct read_args *args = raw;
                pthread_barrier_wait(args->barrier);
                return read(args->fd, args->bytes, 4) == 4 ? NULL : (void *)1;
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                int first = open(argv[1], O_RDONLY);
                if (first < 0) return 11;
                int second = dup(first);
                if (second < 0) return 12;

                pthread_barrier_t barrier;
                pthread_barrier_init(&barrier, NULL, 3);
                struct read_args a = { .fd = first, .barrier = &barrier };
                struct read_args b = { .fd = second, .barrier = &barrier };
                pthread_t first_thread;
                pthread_t second_thread;
                pthread_create(&first_thread, NULL, read_four, &a);
                pthread_create(&second_thread, NULL, read_four, &b);
                pthread_barrier_wait(&barrier);

                void *first_result = NULL;
                void *second_result = NULL;
                pthread_join(first_thread, &first_result);
                pthread_join(second_thread, &second_result);
                pthread_barrier_destroy(&barrier);
                close(second);
                close(first);
                if (first_result || second_result) return 13;

                if (a.bytes[0] == 'a') {
                    fwrite(a.bytes, 1, 4, stdout);
                    fwrite(b.bytes, 1, 4, stdout);
                } else {
                    fwrite(b.bytes, 1, 4, stdout);
                    fwrite(a.bytes, 1, 4, stdout);
                }
                return 0;
            }
            """);
    }

    private static async Task WriteCloseRaceProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <pthread.h>
            #include <stdio.h>
            #include <unistd.h>

            struct read_args {
                int fd;
                char byte;
                ssize_t result;
            };

            static void *read_one(void *raw) {
                struct read_args *args = raw;
                args->result = read(args->fd, &args->byte, 1);
                return NULL;
            }

            int main(int argc, char **argv) {
                if (argc != 4) return 10;
                int managed = open(argv[1], O_RDONLY);
                if (managed < 0) return 11;

                struct read_args args = { .fd = managed };
                pthread_t reader;
                if (pthread_create(&reader, NULL, read_one, &args) != 0) return 12;

                int observed_ensure = 0;
                for (int attempt = 0; attempt < 2000; attempt++) {
                    if (access(argv[3], F_OK) == 0) {
                        observed_ensure = 1;
                        break;
                    }
                    usleep(1000);
                }
                if (!observed_ensure) return 13;

                if (close(managed) != 0) return 14;
                int replacement = open(argv[2], O_RDONLY);
                if (replacement < 0) return 15;
                if (replacement != managed) {
                    if (dup2(replacement, managed) != managed) return 16;
                    close(replacement);
                    replacement = managed;
                }

                if (pthread_join(reader, NULL) != 0) return 17;
                if (args.result != 1 || args.byte != 'a') return 18;
                char replacement_byte = 0;
                if (pread(replacement, &replacement_byte, 1, 0) != 1 || replacement_byte != 'z') return 19;
                close(replacement);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteDup2ReplacementProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <fcntl.h>
            #include <pthread.h>
            #include <stdio.h>
            #include <string.h>
            #include <unistd.h>

            struct read_args {
                int fd;
                pthread_barrier_t *barrier;
                char bytes[4];
                ssize_t result;
            };

            static void *read_target(void *raw) {
                struct read_args *args = raw;
                pthread_barrier_wait(args->barrier);
                args->result = read(args->fd, args->bytes, sizeof(args->bytes));
                return NULL;
            }

            int main(int argc, char **argv) {
                if (argc != 3) return 10;
                int source = open(argv[1], O_RDONLY);
                if (source < 0) return 11;
                int target = open(argv[2], O_RDONLY);
                if (target < 0) return 12;

                pthread_barrier_t barrier;
                pthread_barrier_init(&barrier, NULL, 2);
                struct read_args args = { .fd = target, .barrier = &barrier };
                pthread_t reader;
                pthread_create(&reader, NULL, read_target, &args);
                pthread_barrier_wait(&barrier);
                usleep(50000);

                if (dup2(source, target) != target) return 13;
                pthread_join(reader, NULL);
                pthread_barrier_destroy(&barrier);
                if (args.result != 4 || memcmp(args.bytes, "targ", 4) != 0) return 14;

                char replacement[4];
                if (pread(target, replacement, sizeof(replacement), 0) != 4 ||
                    memcmp(replacement, "sour", 4) != 0) return 15;

                close(target);
                close(source);
                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteFcntlBlockerAsync(string sourcePath)
    {
        await File.WriteAllTextAsync(sourcePath, """
            #define _GNU_SOURCE
            #include <dlfcn.h>
            #include <fcntl.h>
            #include <limits.h>
            #include <stdarg.h>
            #include <stdio.h>
            #include <stdlib.h>
            #include <string.h>
            #include <sys/syscall.h>
            #include <unistd.h>

            static int (*next_fcntl)(int, int, ...) = NULL;
            static int blocked = 0;

            static void block_target_once(int fd, int command) {
                if (command != F_GETFD || blocked) return;
                const char *target = getenv("FREOPEN_BLOCK_TARGET");
                const char *marker = getenv("FREOPEN_BLOCK_MARKER");
                if (!target || !marker) return;

                char descriptor_path[64];
                char resolved[PATH_MAX];
                snprintf(descriptor_path, sizeof(descriptor_path), "/proc/self/fd/%d", fd);
                ssize_t length = readlink(descriptor_path, resolved, sizeof(resolved) - 1);
                if (length <= 0) return;
                resolved[length] = '\0';
                if (strcmp(resolved, target) != 0 ||
                    !__sync_bool_compare_and_swap(&blocked, 0, 1)) return;

                int marker_fd = (int)syscall(
                    SYS_openat,
                    AT_FDCWD,
                    marker,
                    O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC,
                    0600);
                if (marker_fd >= 0) syscall(SYS_close, marker_fd);
                usleep(300000);
            }

            int fcntl(int fd, int command, ...) {
                if (!next_fcntl) next_fcntl = dlsym(RTLD_NEXT, "fcntl");
                block_target_once(fd, command);
                switch (command) {
                    case F_GETFD:
                    case F_GETFL:
                    case F_GETOWN:
                        return next_fcntl(fd, command);
                    case F_SETFD:
                    case F_SETFL:
                    case F_SETOWN: {
                        va_list arguments;
                        va_start(arguments, command);
                        int value = va_arg(arguments, int);
                        va_end(arguments);
                        return next_fcntl(fd, command, value);
                    }
                    default: {
                        va_list arguments;
                        va_start(arguments, command);
                        void *value = va_arg(arguments, void *);
                        va_end(arguments);
                        return next_fcntl(fd, command, value);
                    }
                }
            }
            """);
    }

    private static async Task WriteFreopenAtomicProbeAsync(string sourcePath)
    {
        await File.WriteAllTextAsync(sourcePath, """
            #define _GNU_SOURCE
            #include <pthread.h>
            #include <stdio.h>
            #include <unistd.h>

            struct read_args {
                int fd;
                const char *marker;
                char byte;
                ssize_t result;
            };

            static void *read_during_replacement(void *raw) {
                struct read_args *args = raw;
                for (int attempt = 0; attempt < 2000; attempt++) {
                    if (access(args->marker, F_OK) == 0) {
                        args->result = pread(args->fd, &args->byte, 1, 0);
                        return NULL;
                    }
                    usleep(1000);
                }
                args->result = -2;
                return NULL;
            }

            int main(int argc, char **argv) {
                if (argc != 3) return 10;
                FILE *stream = tmpfile();
                if (!stream) return 11;
                int descriptor = fileno(stream);

                struct read_args args = {
                    .fd = descriptor,
                    .marker = argv[2]
                };
                pthread_t reader;
                if (pthread_create(&reader, NULL, read_during_replacement, &args) != 0) return 12;

                FILE *replaced = freopen(argv[1], "rb", stream);
                if (!replaced || fileno(replaced) != descriptor) return 13;
                if (pthread_join(reader, NULL) != 0) return 14;
                if (args.result != 1 || args.byte != 'a') return 15;
                if (fclose(replaced) != 0) return 16;

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteCloseRangeProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <limits.h>
            #include <pthread.h>
            #include <stdio.h>
            #include <unistd.h>

            #ifndef CLOSE_RANGE_UNSHARE
            #define CLOSE_RANGE_UNSHARE (1U << 1)
            #endif
            #ifndef CLOSE_RANGE_CLOEXEC
            #define CLOSE_RANGE_CLOEXEC (1U << 2)
            #endif

            struct read_args {
                int fd;
                pthread_barrier_t *barrier;
                char byte;
                ssize_t result;
            };

            static void *read_after_unshare(void *raw) {
                struct read_args *args = raw;
                pthread_barrier_wait(args->barrier);
                args->result = read(args->fd, &args->byte, 1);
                return NULL;
            }

            int main(int argc, char **argv) {
                if (argc != 2) return 10;

                int shared = open(argv[1], O_RDONLY);
                if (shared < 0) return 18;
                pthread_barrier_t barrier;
                pthread_barrier_init(&barrier, NULL, 2);
                struct read_args args = { .fd = shared, .barrier = &barrier };
                pthread_t sibling;
                if (pthread_create(&sibling, NULL, read_after_unshare, &args) != 0) return 19;
                errno = 0;
                if (close_range(
                        (unsigned int)shared,
                        (unsigned int)shared,
                        CLOSE_RANGE_UNSHARE) == 0 ||
                    errno != EOPNOTSUPP) return 20;
                pthread_barrier_wait(&barrier);
                pthread_join(sibling, NULL);
                pthread_barrier_destroy(&barrier);
                if (args.result != 1 || args.byte != 'a') return 21;
                close(shared);

                int first = open(argv[1], O_RDONLY);
                if (first < 0) return 11;
                if (close_range((unsigned int)first, (unsigned int)first, 0) != 0) return 12;

                int reused = open("/dev/zero", O_RDONLY);
                if (reused != first) return 13;
                char byte = 1;
                if (read(reused, &byte, 1) != 1 || byte != 0) return 14;
                close(reused);

                int cloexec = open(argv[1], O_RDONLY);
                if (cloexec < 0) return 15;
                if (close_range(
                        (unsigned int)cloexec,
                        (unsigned int)cloexec,
                        CLOSE_RANGE_CLOEXEC) != 0) return 16;
                byte = 0;
                if (read(cloexec, &byte, 1) != 1 || byte != 'a') return 17;
                close(cloexec);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteBulkCloseProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <stdio.h>
            #include <unistd.h>

            int main(int argc, char **argv) {
                if (argc != 2) return 10;

                int descriptor = open(argv[1], O_RDONLY);
                if (descriptor < 0) return 11;
                closefrom(descriptor);
                int reused = open("/dev/zero", O_RDONLY);
                if (reused != descriptor) return 12;
                char byte = 1;
                if (read(reused, &byte, 1) != 1 || byte != 0) return 13;
                close(reused);

                FILE *stream = fopen(argv[1], "rb");
                if (!stream) return 14;
                int stream_descriptor = fileno(stream);
                if (fcloseall() != 0) return 15;

                errno = 0;
                if (fcntl(stream_descriptor, F_GETFD) >= 0) {
                    byte = 1;
                    errno = 0;
                    if (read(stream_descriptor, &byte, 1) != -1 || errno != EIO) return 16;
                    if (close(stream_descriptor) != 0) return 17;
                    return 0;
                }
                if (errno != EBADF) return 18;

                int fillers[64];
                int filler_count = 0;
                int reopened = -1;
                for (int attempt = 0; attempt < 100 && reopened != stream_descriptor; attempt++) {
                    reopened = open("/dev/zero", O_RDONLY);
                    if (reopened < 0) return 19;
                    if (reopened <= stream_descriptor) {
                        fillers[filler_count++] = reopened;
                        continue;
                    }

                    close(reopened);
                    while (filler_count > 0) close(fillers[--filler_count]);
                    reopened = -1;
                    usleep(10000);
                }
                if (reopened != stream_descriptor) return 20;
                byte = 1;
                if (read(reopened, &byte, 1) != 1 || byte != 0) return 21;
                for (int index = 0; index < filler_count; index++) close(fillers[index]);
                return 0;
            }
            """);
    }

    private static async Task WriteExtentSeekProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #define _LARGEFILE64_SOURCE
            #include <fcntl.h>
            #include <linux/fiemap.h>
            #include <linux/fs.h>
            #include <stdio.h>
            #include <string.h>
            #include <sys/ioctl.h>
            #include <unistd.h>

            int main(int argc, char **argv) {
                if (argc != 2) return 10;

                int descriptor = open(argv[1], O_RDONLY);
                if (descriptor < 0) return 11;
                if (lseek(descriptor, 0, SEEK_DATA) != 0) return 12;
                close(descriptor);

                descriptor = open(argv[1], O_RDONLY);
                if (descriptor < 0) return 13;
                if (lseek64(descriptor, 0, SEEK_HOLE) < 8) return 14;
                close(descriptor);

                descriptor = open(argv[1], O_RDONLY);
                if (descriptor < 0) return 15;
                struct fiemap mapping;
                memset(&mapping, 0, sizeof(mapping));
                mapping.fm_start = 0;
                mapping.fm_length = FIEMAP_MAX_OFFSET;
                (void)ioctl(descriptor, FS_IOC_FIEMAP, &mapping);
                close(descriptor);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteOpenFailureProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <fcntl.h>
            #include <stdio.h>
            #include <unistd.h>

            int main(int argc, char **argv) {
                if (argc != 4) return 10;

                errno = 0;
                if (open(argv[1], O_RDONLY) >= 0 || errno != ENOENT) return 11;

                errno = 0;
                FILE *file = fopen64(argv[2], "rb");
                if (file || errno != EIO) return 12;

                errno = 0;
                if (open(argv[3], O_RDONLY) >= 0 || errno != EIO) return 13;

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteFerrorProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <stdio.h>

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                FILE *file = fopen64(argv[1], "rb");
                if (!file) return 11;

                char byte;
                errno = 0;
                if (fread(&byte, 1, 1, file) != 0 || errno != EIO || !ferror(file)) return 12;
                clearerr(file);
                if (ferror(file)) return 13;

                errno = 0;
                if (fread_unlocked(&byte, 1, 1, file) != 0 || errno != EIO || !ferror_unlocked(file)) return 14;
                fclose(file);

                puts("ok");
                return 0;
            }
            """);
    }

    private static async Task WriteReadProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <stdio.h>

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                FILE *file = fopen64(argv[1], "rb");
                if (!file) return 11;

                char buffer[32];
                size_t read = fread(buffer, 1, sizeof(buffer), file);
                fclose(file);

                if (read == 0) return 12;
                fwrite(buffer, 1, read, stdout);
                return 0;
            }
            """);
    }

    private static async Task WriteErrnoProbeAsync(string probeSourcePath)
    {
        await File.WriteAllTextAsync(probeSourcePath, """
            #define _GNU_SOURCE
            #include <errno.h>
            #include <stdio.h>

            int main(int argc, char **argv) {
                if (argc != 2) return 10;
                errno = 0;
                FILE *file = fopen64(argv[1], "rb");
                if (file) {
                    fclose(file);
                    return 12;
                }

                printf("%d\n", errno);
                return 0;
            }
            """);
    }

    private static string FindRepoFile(string relativePath)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.");
    }

    internal static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        bool expectSuccess = true,
        TimeSpan? timeout = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var processTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var timedOut = false;
        using (var cancellation = new CancellationTokenSource(processTimeout))
        {
            try
            {
                await process.WaitForExitAsync(cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                timedOut = true;
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (timedOut)
        {
            Assert.Fail(
                $"Process '{fileName}' did not exit within {processTimeout}.\n" +
                $"stdout:\n{stdout}\n" +
                $"stderr:\n{stderr}");
        }

        if (expectSuccess && process.ExitCode != 0)
        {
            Assert.Fail(
                $"Process '{fileName}' failed with exit code {process.ExitCode}.\n" +
                $"stdout:\n{stdout}\n" +
                $"stderr:\n{stderr}");
        }

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}

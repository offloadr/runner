using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Tests;

public class VfsIpcServerLinuxTests
{
    [Test]
    [SupportedOSPlatform("linux")]
    public void EnsureSocketPathAncestorsAccessible_RefusesAProtectedAncestorWithoutChangingIt()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), $"vfs-socket-ancestors-{Guid.NewGuid():N}");
        var protectedDirectory = Directory.CreateDirectory(Path.Combine(root, "private")).FullName;
        var socketDirectory = Directory.CreateDirectory(Path.Combine(protectedDirectory, "runner-agent")).FullName;
        var protectedMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        File.SetUnixFileMode(protectedDirectory, protectedMode);
        try
        {
            Assert.That(
                () => VfsIpcServer.EnsureSocketPathAncestorsAccessible(socketDirectory, createdByAgent: true),
                Throws.InvalidOperationException.With.Message.Contains(protectedDirectory));
            Assert.That(File.GetUnixFileMode(protectedDirectory), Is.EqualTo(protectedMode));
        }
        finally
        {
            File.SetUnixFileMode(protectedDirectory, protectedMode | UnixFileMode.OtherExecute);
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public void EnsureSocketPathAncestorsAccessible_MakesADirectoryItCreatedSearchable()
    {
        LinuxTestPrerequisites.RequireLinux();
        var root = Path.Combine(Path.GetTempPath(), $"vfs-socket-created-{Guid.NewGuid():N}");
        var socketDirectory = Directory.CreateDirectory(Path.Combine(root, "runner-agent")).FullName;
        File.SetUnixFileMode(socketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            VfsIpcServer.EnsureSocketPathAncestorsAccessible(socketDirectory, createdByAgent: true);

            Assert.That(File.GetUnixFileMode(socketDirectory) & UnixFileMode.OtherExecute, Is.EqualTo(UnixFileMode.OtherExecute));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task Start_ValidRequest_InvokesHandlerAndReturnsSuccess()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var requests = new List<VfsIpcRequest>();
        var socketPath = CreateSocketPath();

        await using var server = new VfsIpcServer(socketPath, (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new VfsIpcResponse(
                VfsIpcStatus.Success,
                VfsIpcOpenDisposition.RangeManaged,
                LeaseId: 42,
                TransferEpoch: 7,
                ExpectedLength: 8_000_000_000,
                DeviceId: 0x1234,
                Inode: 0x5678));
        });

        server.Start();
        using var client = await ConnectAsync(socketPath);

        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.Open("/models/base.safetensors", "session-1"),
            CancellationToken.None);
        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.That(response, Is.Not.Null);
        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
        Assert.That(response.Value.OpenDisposition, Is.EqualTo(VfsIpcOpenDisposition.RangeManaged));
        Assert.That(response.Value.LeaseId, Is.EqualTo(42));
        Assert.That(response.Value.TransferEpoch, Is.EqualTo(7));
        Assert.That(response.Value.ExpectedLength, Is.EqualTo(8_000_000_000));
        Assert.That(response.Value.DeviceId, Is.EqualTo(0x1234));
        Assert.That(response.Value.Inode, Is.EqualTo(0x5678));
        Assert.That(requests, Has.Count.EqualTo(1));
        Assert.That(requests[0].Path, Is.EqualTo("/models/base.safetensors"));
        Assert.That(requests[0].SessionId, Is.EqualTo("session-1"));
    }

    [Test]
    public async Task Start_LargeRange_RoundTrips64BitFields()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        VfsIpcRequest? observed = null;
        await using var server = new VfsIpcServer(socketPath, (request, _) =>
        {
            observed = request;
            return Task.FromResult(VfsIpcResponse.Success(transferEpoch: 12));
        });
        server.Start();

        using var client = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.EnsureRange(
                leaseId: ulong.MaxValue - 2,
                transferEpoch: 11,
                offset: (long)int.MaxValue + 4096L,
                length: (long)int.MaxValue + 8192L),
            CancellationToken.None);
        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(observed, Is.Not.Null);
            Assert.That(observed!.Value.LeaseId, Is.EqualTo(ulong.MaxValue - 2));
            Assert.That(observed.Value.TransferEpoch, Is.EqualTo(11));
            Assert.That(observed.Value.Offset, Is.EqualTo((long)int.MaxValue + 4096L));
            Assert.That(observed.Value.Length, Is.EqualTo((long)int.MaxValue + 8192L));
            Assert.That(response!.Value.TransferEpoch, Is.EqualTo(12));
        });
    }

    [Test]
    public async Task Start_InvalidLength_ReturnsInvalidLengthStatus()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();

        await using var server = new VfsIpcServer(
            socketPath,
            (_, _) => Task.FromResult(VfsIpcResponse.Success()));
        server.Start();

        using var client = await ConnectAsync(socketPath);
        var header = new byte[VfsIpcProtocol.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x4f564653);
        header[4] = VfsIpcProtocol.Version;
        header[5] = (byte)VfsIpcOperation.Open;
        BinaryPrimitives.WriteUInt32BigEndian(
            header.AsSpan(8, 4),
            VfsIpcProtocol.MaxPayloadBytes + 1u);
        await SendExactAsync(client, header, CancellationToken.None);

        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.InvalidLength));
    }

    [Test]
    public async Task Start_ShortBody_ReturnsShortBodyStatus()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();

        await using var server = new VfsIpcServer(
            socketPath,
            (_, _) => Task.FromResult(VfsIpcResponse.Success()));
        server.Start();

        using var client = await ConnectAsync(socketPath);

        var header = new byte[VfsIpcProtocol.HeaderLength];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), 0x4f564653);
        header[4] = VfsIpcProtocol.Version;
        header[5] = (byte)VfsIpcOperation.Open;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8, 4), 10u);
        await SendExactAsync(client, header, CancellationToken.None);
        await SendExactAsync(client, "abc"u8.ToArray(), CancellationToken.None);
        client.Shutdown(SocketShutdown.Send);

        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.ShortBody));
    }

    [Test]
    public async Task Start_HandlerFailure_ReturnsEnsureFailedStatus()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();

        await using var server = new VfsIpcServer(socketPath, (_, _) => throw new InvalidOperationException("failed"));
        server.Start();

        using var client = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.Open("/models/fail.bin"),
            CancellationToken.None);

        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.EnsureFailed));
    }

    [Test]
    public async Task Start_HydrationIoFailure_ReturnsAuthoritativeEpoch()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        await using var server = new VfsIpcServer(
            socketPath,
            (_, _) => throw new ModelHydrationIOException(
                "immutable range invalidated",
                transferEpoch: 9));
        server.Start();

        using var client = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.EnsureRange(leaseId: 7, transferEpoch: 8, offset: 4, length: 4),
            CancellationToken.None);
        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.IoError));
        Assert.That(response.Value.TransferEpoch, Is.EqualTo(9));
    }

    [Test]
    public async Task Start_HandlerReturnsNotManaged_ReturnsNotManagedStatus()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();

        await using var server = new VfsIpcServer(
            socketPath,
            (_, _) => Task.FromResult(VfsIpcResponse.Error(VfsIpcStatus.NotManaged)));
        server.Start();

        using var client = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.Open("/models/optional.json"),
            CancellationToken.None);

        var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);

        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.NotManaged));
    }

    [Test]
    public async Task Start_IdleClientReadDeadline_ReleasesBoundedClientSlot()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        await using var server = new VfsIpcServer(
            socketPath,
            (_, _) => Task.FromResult(VfsIpcResponse.Success()),
            requestReadTimeout: TimeSpan.FromMilliseconds(100),
            maxConcurrentClients: 1);
        server.Start();

        using var idleClient = await ConnectAsync(socketPath);
        var timeoutResponse = await VfsIpcProtocol
            .ReadResponseAsync(idleClient, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        using var validClient = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            validClient,
            VfsIpcRequest.Open("/models/after-timeout.bin"),
            CancellationToken.None);
        var validResponse = await VfsIpcProtocol
            .ReadResponseAsync(validClient, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(timeoutResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.Cancelled));
            Assert.That(validResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
        });
    }

    [Test]
    public async Task Start_MaxConcurrentClients_BoundsLiveHandlers()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;
        await using var server = new VfsIpcServer(
            socketPath,
            async (_, _) =>
            {
                var invocation = Interlocked.Increment(ref invocationCount);
                if (invocation == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task.ConfigureAwait(false);
                }
                else
                {
                    secondStarted.TrySetResult();
                }

                return VfsIpcResponse.Success();
            },
            maxConcurrentClients: 1);
        server.Start();

        using var firstClient = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            firstClient,
            VfsIpcRequest.Open("/models/first.bin"),
            CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var secondClient = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            secondClient,
            VfsIpcRequest.Open("/models/second.bin"),
            CancellationToken.None);
        await Task.Delay(150);
        Assert.That(Volatile.Read(ref invocationCount), Is.EqualTo(1));

        releaseFirst.TrySetResult();
        var firstResponse = await VfsIpcProtocol
            .ReadResponseAsync(firstClient, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var secondResponse = await VfsIpcProtocol
            .ReadResponseAsync(secondClient, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(firstResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
            Assert.That(secondResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
            Assert.That(Volatile.Read(ref invocationCount), Is.EqualTo(2));
        });
    }

    [Test]
    public async Task Start_FailedRequests_LogEscapedBoundedPathsAtALimitedRate()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var logs = new List<string>();
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddProvider(new RecordingLoggerProvider(logs));
            builder.SetMinimumLevel(LogLevel.Information);
        });
        RunnerLog.Configure(loggerFactory);
        try
        {
            var socketPath = CreateSocketPath();
            await using var server = new VfsIpcServer(
                socketPath,
                (request, _) => throw new InvalidOperationException($"cannot open '{request.Path}'"));
            server.Start();

            var forgedPath = "/models/x.bin\n2026-01-01 [INF] forged entry\u001b[2J" + new string('p', 5000);
            for (var attempt = 0; attempt < 60; attempt++)
            {
                using var client = await ConnectAsync(socketPath);
                await VfsIpcProtocol.WriteRequestAsync(client, VfsIpcRequest.Open(forgedPath, "session-1"), CancellationToken.None);
                var response = await VfsIpcProtocol.ReadResponseAsync(client, CancellationToken.None);
                Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.EnsureFailed));
            }

            string[] failures;
            lock (logs)
            {
                failures = logs.Where(static line => line.Contains("[vfs-ipc] Operation Open failed", StringComparison.Ordinal)).ToArray();
            }

            Assert.Multiple(() =>
            {
                Assert.That(failures, Is.Not.Empty);
                Assert.That(failures, Has.Length.LessThanOrEqualTo(20));
                Assert.That(failures.Any(static line => line.Any(char.IsControl)), Is.False);
                Assert.That(failures.All(static line => line.Contains(@"x.bin\u000a2026", StringComparison.Ordinal)), Is.True);
                Assert.That(failures.All(static line => line.Length < 6000), Is.True);
                Assert.That(failures.All(static line => !line.Contains(new string('p', 600), StringComparison.Ordinal)), Is.True);
            });
        }
        finally
        {
            RunnerLog.Configure(null);
        }
    }

    private sealed class RecordingLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(lines);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                // What a console sink would print: the message plus any attached exception.
                var line = formatter(state, exception) + (exception is null ? string.Empty : " " + exception);
                lock (lines)
                {
                    lines.Add(line);
                }
            }
        }
    }

    [Test]
    public async Task Start_ClientHangsUpWhileWaiting_CancelsHandlerAndFreesSlot()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new VfsIpcServer(
            socketPath,
            async (request, cancellationToken) =>
            {
                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    using var registration = cancellationToken.Register(() => cancelled.TrySetResult());
                    waiting.TrySetResult();
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return VfsIpcResponse.Success();
            },
            maxConcurrentClients: 1);
        server.Start();

        var waitingClient = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            waitingClient,
            VfsIpcRequest.EnsureRange(leaseId: 5, transferEpoch: 1, offset: 0, length: 4),
            CancellationToken.None);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
        waitingClient.Dispose();

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var nextClient = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(nextClient, VfsIpcRequest.Release(5), CancellationToken.None);
        var response = await VfsIpcProtocol
            .ReadResponseAsync(nextClient, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
    }

    [Test]
    public async Task Start_ConnectedClientWaiting_IsNotCancelled()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new VfsIpcServer(
            socketPath,
            async (_, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return VfsIpcResponse.Success(transferEpoch: 3);
            });
        server.Start();

        using var client = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.EnsureComplete(leaseId: 5, transferEpoch: 1, reason: "mapping"),
            CancellationToken.None);
        await Task.Delay(200);
        release.TrySetResult();
        var response = await VfsIpcProtocol
            .ReadResponseAsync(client, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Multiple(() =>
        {
            Assert.That(response!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
            Assert.That(response.Value.TransferEpoch, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Start_ClientDisconnectsBeforeOpenResponse_ReleasesRangeManagedLease()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        var openHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasedLease = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new VfsIpcServer(socketPath, async (request, _) =>
        {
            if (request.Operation == VfsIpcOperation.Release)
            {
                releasedLease.TrySetResult(request.LeaseId);
                return VfsIpcResponse.Success();
            }

            openHandled.TrySetResult();
            await allowResponse.Task.ConfigureAwait(false);
            return new VfsIpcResponse(
                VfsIpcStatus.Success,
                VfsIpcOpenDisposition.RangeManaged,
                LeaseId: 77,
                TransferEpoch: 2,
                ExpectedLength: 8);
        });
        server.Start();

        var client = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            client,
            VfsIpcRequest.Open("/models/disconnected.bin", "session-1"),
            CancellationToken.None);
        await openHandled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        client.LingerState = new LingerOption(enable: true, seconds: 0);
        client.Dispose();
        allowResponse.TrySetResult();

        Assert.That(
            await releasedLease.Task.WaitAsync(TimeSpan.FromSeconds(2)),
            Is.EqualTo(77));
    }

    [Test]
    public async Task Start_UnacknowledgedOpenResponse_ExpiresRangeManagedLease()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        var releasedLease = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ensureInvoked = false;
        await using var server = new VfsIpcServer(
            socketPath,
            (request, _) =>
            {
                if (request.Operation == VfsIpcOperation.Release)
                {
                    releasedLease.TrySetResult(request.LeaseId);
                    return Task.FromResult(VfsIpcResponse.Success());
                }

                if (request.Operation == VfsIpcOperation.EnsureRange)
                {
                    ensureInvoked = true;
                    return Task.FromResult(VfsIpcResponse.Success());
                }

                return Task.FromResult(new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.RangeManaged,
                    LeaseId: 78,
                    TransferEpoch: 2,
                    ExpectedLength: 8));
            },
            openAcknowledgementTimeout: TimeSpan.FromMilliseconds(100));
        server.Start();

        using (var openClient = await ConnectAsync(socketPath))
        {
            await VfsIpcProtocol.WriteRequestAsync(
                openClient,
                VfsIpcRequest.Open("/models/unacknowledged.bin", "session-1"),
                CancellationToken.None);
            var openResponse = await VfsIpcProtocol.ReadResponseAsync(
                openClient,
                CancellationToken.None);
            Assert.That(openResponse!.Value.LeaseId, Is.EqualTo(78));
        }

        using (var ensureClient = await ConnectAsync(socketPath))
        {
            await VfsIpcProtocol.WriteRequestAsync(
                ensureClient,
                VfsIpcRequest.EnsureRange(78, 2, 0, 4),
                CancellationToken.None);
            var ensureResponse = await VfsIpcProtocol.ReadResponseAsync(
                ensureClient,
                CancellationToken.None);
            Assert.That(ensureResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.IoError));
        }

        var released = await releasedLease.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Multiple(() =>
        {
            Assert.That(released, Is.EqualTo(78));
            Assert.That(ensureInvoked, Is.False);
        });
    }

    [Test]
    public async Task Start_AcknowledgedOpenResponse_DoesNotExpireRangeManagedLease()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();
        long releasedLease = 0;
        await using var server = new VfsIpcServer(
            socketPath,
            (request, _) =>
            {
                if (request.Operation == VfsIpcOperation.Release)
                {
                    Interlocked.Exchange(ref releasedLease, checked((long)request.LeaseId));
                    return Task.FromResult(VfsIpcResponse.Success());
                }

                return Task.FromResult(new VfsIpcResponse(
                    VfsIpcStatus.Success,
                    VfsIpcOpenDisposition.RangeManaged,
                    LeaseId: 79,
                    TransferEpoch: 2,
                    ExpectedLength: 8));
            },
            openAcknowledgementTimeout: TimeSpan.FromMilliseconds(100));
        server.Start();

        using (var openClient = await ConnectAsync(socketPath))
        {
            await VfsIpcProtocol.WriteRequestAsync(
                openClient,
                VfsIpcRequest.Open("/models/acknowledged.bin", "session-1"),
                CancellationToken.None);
            var openResponse = await VfsIpcProtocol.ReadResponseAsync(
                openClient,
                CancellationToken.None);
            Assert.That(openResponse!.Value.LeaseId, Is.EqualTo(79));
        }

        using (var acknowledgementClient = await ConnectAsync(socketPath))
        {
            await VfsIpcProtocol.WriteRequestAsync(
                acknowledgementClient,
                VfsIpcRequest.AcknowledgeOpen(79),
                CancellationToken.None);
            var acknowledgementResponse = await VfsIpcProtocol.ReadResponseAsync(
                acknowledgementClient,
                CancellationToken.None);
            Assert.That(acknowledgementResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
        }

        await Task.Delay(250);
        Assert.That(Volatile.Read(ref releasedLease), Is.Zero);

        using var releaseClient = await ConnectAsync(socketPath);
        await VfsIpcProtocol.WriteRequestAsync(
            releaseClient,
            VfsIpcRequest.Release(79),
            CancellationToken.None);
        var releaseResponse = await VfsIpcProtocol.ReadResponseAsync(
            releaseClient,
            CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(releaseResponse!.Value.Status, Is.EqualTo(VfsIpcStatus.Success));
            Assert.That(Volatile.Read(ref releasedLease), Is.EqualTo(79));
        });
    }

    [Test]
    [SupportedOSPlatform("linux")]
    public async Task Start_SocketAllowsIsolatedSessionUsers()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        // The server creates its own socket directory beneath a world-searchable parent.
        var root = Path.Combine(Path.GetTempPath(), "runneragent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        var socketDirectory = Path.Combine(root, "runner-agent");
        var socketPath = Path.Combine(socketDirectory, "model-fetch.sock");

        await using var server = new VfsIpcServer(
            socketPath,
            (_, _) => Task.FromResult(VfsIpcResponse.Success()));
        server.Start();

        var directoryMode = File.GetUnixFileMode(socketDirectory);
        var socketMode = File.GetUnixFileMode(socketPath);

        Assert.Multiple(() =>
        {
            Assert.That(
                directoryMode & (UnixFileMode.GroupExecute | UnixFileMode.OtherExecute),
                Is.EqualTo(UnixFileMode.GroupExecute | UnixFileMode.OtherExecute));
            Assert.That(
                directoryMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite),
                Is.EqualTo((UnixFileMode)0));
            Assert.That(
                socketMode & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                              UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                              UnixFileMode.OtherRead | UnixFileMode.OtherWrite),
                Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite |
                           UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                           UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        });
    }

    [Test]
    public async Task Dispose_RemovesSocketFile()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireUnixDomainSockets();

        var socketPath = CreateSocketPath();

        var server = new VfsIpcServer(
            socketPath,
            (_, _) => Task.FromResult(VfsIpcResponse.Success()));
        server.Start();
        Assert.That(File.Exists(socketPath), Is.True);

        await server.DisposeAsync();

        Assert.That(File.Exists(socketPath), Is.False);
    }

    private static string CreateSocketPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "runneragent-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return Path.Combine(root, "model-fetch.sock");
    }

    private static async Task<Socket> ConnectAsync(string socketPath)
    {
        Exception? last = null;

        for (var attempt = 0; attempt < 20; attempt++)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath));
                return socket;
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
                await Task.Delay(25);
            }
        }

        throw new InvalidOperationException($"Failed to connect to unix socket '{socketPath}'", last);
    }

    private static async Task SendExactAsync(Socket socket, byte[] payload, CancellationToken cancellationToken)
    {
        var sent = 0;
        while (sent < payload.Length)
        {
            var segment = new ArraySegment<byte>(payload, sent, payload.Length - sent);
            var wrote = await socket.SendAsync(segment, SocketFlags.None, cancellationToken);
            if (wrote == 0)
            {
                throw new IOException("Socket closed while writing test payload.");
            }

            sent += wrote;
        }
    }
}

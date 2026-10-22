using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

[NonParallelizable]
public sealed class CommandStreamReconnectTests
{
    private const int Failures = 3;
    private const string RunnerId = "11111111111141118111111111111111";
    private const string InstanceId = "22222222222242228222222222222222";
    private const string Version = "command-stream-regression";
    private const string SyntheticSecret = "owned-loopback-no-application-authority";
    private sealed record Arrival(int Ordinal, long Timestamp, string Protocol, string FrameSha256, bool MatchesOriginal);
    private sealed record Log(long Timestamp, string Category, string Level, string Message);

    [TestCase("unavailable-before-message")]
    [TestCase("empty-ok")]
    [TestCase("keepalive-then-unavailable")]
    public async Task CommandStream_RetainsFailureStreakUntilAMessageArrives(string mode)
    {
        var output = Path.Combine(Path.GetTempPath(), "runner-command-stream-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(output);
        try { await CaptureAsync(mode, output); }
        finally { Directory.Delete(output, recursive: true); }
    }

    private static async Task CaptureAsync(string mode, string output)
    {
        var scratch = Path.Combine(output, mode + "-scratch");
        Directory.CreateDirectory(scratch);
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var arrivals = new ConcurrentQueue<Arrival>();
        var statusTimes = new ConcurrentQueue<long>();
        var logs = new CaptureLogger();
        var opened = new ConcurrentDictionary<string, byte>();
        var closed = new ConcurrentDictionary<string, byte>();
        var exited = new ConcurrentDictionary<string, byte>();
        var registrations = new ConcurrentBag<CancellationTokenRegistration>();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var heldExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandRequests = 0;
        var unexpectedRequests = 0;
        var keepaliveMessagesWritten = 0;
        var options = SessionProcessOptions.FromEnvironment(name => name switch
        {
            "SESSION_ROOT" => Path.Combine(scratch, "sessions"),
            "COMFY_WORKDIR" => scratch,
            "COMFY_ENTRYPOINT" => Path.Combine(scratch, "must-not-exist"),
            "RUNNER_SEALED_STORAGE" => "off",
            _ => null,
        });
        var effects = new ForbiddenEffects();
        using var manager = new SessionProcessManager(options, effects, new RunnerVfsEnvironmentBuilder(), effects);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.Use(next => async connection =>
            {
                opened.TryAdd(connection.ConnectionId, 0);
                registrations.Add(connection.ConnectionClosed.Register(() => closed.TryAdd(connection.ConnectionId, 0)));
                try { await next(connection); }
                finally { exited.TryAdd(connection.ConnectionId, 0); }
            });
        }));
        await using var app = builder.Build();
        var service = "/" + RunnerControlService.Descriptor.FullName + "/";
        app.MapPost(service + "RegisterRunner", async context =>
        {
            var frame = await ReadRequestAsync(context);
            var request = RegisterRunnerRequest.Parser.ParseFrom(frame.AsSpan(5));
            Require(request.RunnerId == RunnerId && request.InstanceId == InstanceId && request.Version == Version
                && request.ActiveSessionId.Length == 0, "Wrong synthetic status identity.");
            statusTimes.Enqueue(Stopwatch.GetTimestamp());
            await WriteMessageAsync(context, new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 });
            context.Response.AppendTrailer("grpc-status", "0");
        });
        app.MapPost(service + "CommandStream", async context =>
        {
            var frame = await ReadRequestAsync(context);
            var request = CommandStreamRequest.Parser.ParseFrom(frame.AsSpan(5));
            var ordinal = Interlocked.Increment(ref commandRequests);
            arrivals.Enqueue(new(ordinal, Stopwatch.GetTimestamp(), context.Request.Protocol,
                Convert.ToHexStringLower(SHA256.HashData(frame)), request.RunnerId == RunnerId && request.InstanceId == InstanceId && request.Version == Version));
            context.Response.ContentType = "application/grpc";
            if (ordinal <= Failures)
            {
                if (mode == "keepalive-then-unavailable")
                {
                    await WriteMessageAsync(context, new CommandStreamResponse { Keepalive = new CommandStreamKeepAlive() });
                    Interlocked.Increment(ref keepaliveMessagesWritten);
                    context.Response.AppendTrailer("grpc-status", "14");
                }
                else context.Response.Headers["grpc-status"] = mode == "empty-ok" ? "0" : "14";
                return;
            }
            Require(ordinal == Failures + 1, "Unexpected extra command-stream request.");
            await context.Response.StartAsync(context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            held.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            finally { heldExited.TrySetResult(); }
        });
        app.MapFallback(context =>
        {
            Interlocked.Increment(ref unexpectedRequests);
            context.Response.StatusCode = 404;
            return Task.CompletedTask;
        });
        await app.StartAsync(lifetime.Token);
        var endpoint = app.Urls.Single();
        using var channel = GrpcChannelManager.CreateChannel(endpoint, false);
        var invoker = channel.Intercept(new RunnerSecretClientInterceptor(SyntheticSecret));
        var identities = new RuntimeIdentityRegistry();
        var settings = Aria2Settings.FromEnvironment(name => name switch
        {
            "ARIA2_DOWNLOAD_DIR" => Path.Combine(scratch, "downloads"),
            "ARIA2_STATE_DIR" => Path.Combine(scratch, "transfers"),
            _ => null,
        });
        await using var transfers = new UnusedModelTransfers();
        await using var downloads = new ModelDownloadService(transfers, settings);
        await using var artifacts = new ArtifactUploadService(SyntheticSecret,
            new RunnerArtifactService.RunnerArtifactServiceClient(invoker), NullLogger<ArtifactUploadService>.Instance, identities, RunnerId, InstanceId);
        await using var workspace = new WorkspaceMirrorService(new RunnerWorkspaceService.RunnerWorkspaceServiceClient(invoker), SyntheticSecret,
            NullLogger<WorkspaceMirrorService>.Instance, runtimeIdentities: identities, runnerId: RunnerId, instanceId: InstanceId);
        await using var events = new ComfySessionEventRelay(RunnerId, "127.0.0.1", 1, InstanceId,
            manager.GetSessionSealingStatus, manager.IsTrackedRuntime, artifacts.CaptureSessionRefresh);
        await using var processLogs = new SessionProcessLogRelay(RunnerId, identities);
        await using var runnerLogs = new RunnerLogRelay(RunnerId);
        await using var telemetry = new SessionRuntimeTelemetryRelay(RunnerId, identities);
        var power = new GpuPowerCommandExecutor(new GpuPowerLimitService(new RuntimeTelemetryService(effects), effects));
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(logs));
        RunnerLog.Configure(loggerFactory);
        var loop = ServiceClientManager.CommandListenerLoopAsync(endpoint, false, SyntheticSecret, RunnerId, InstanceId,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(1600), TimeSpan.FromSeconds(5), Version,
            manager, downloads, events, processLogs, runnerLogs, telemetry, power, artifacts, workspace,
            new ServiceClientManager.LogicalSessionState(string.Empty), new RunnerResources(), [], null, null, null,
            TimeSpan.FromMinutes(1), identities, shutdown.Token);
        var joined = false;
        var listenerRefused = false;
        try
        {
            await held.Task.WaitAsync(lifetime.Token);
            while (statusTimes.Count < 2) await Task.Delay(10, lifetime.Token);
            await shutdown.CancelAsync();
            await loop.WaitAsync(lifetime.Token);
            joined = true;
            await heldExited.Task.WaitAsync(lifetime.Token);
            await Task.Delay(250, lifetime.Token);
            Require(commandRequests == Failures + 1 && arrivals.All(a => a.MatchesOriginal && a.Protocol == "HTTP/2"), "Wrong command count or identity.");
            Require(arrivals.Select(a => a.FrameSha256).Distinct().Count() == 1, "Reconnect changed original request bytes.");
            Require(unexpectedRequests == 0 && effects.Calls == 0 && transfers.UnexpectedCalls == 0
                && manager.GetActiveSessionId().Length == 0, "Unexpected effect or route.");
            Require(statusTimes.Count >= 2, "Independent status lane did not progress.");
        }
        finally
        {
            await shutdown.CancelAsync();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
            channel.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            foreach (var registration in registrations) registration.Dispose();
            RunnerLog.Configure(null);
            using var socket = new TcpClient();
            try { await socket.ConnectAsync(IPAddress.Loopback, new Uri(endpoint).Port); }
            catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionRefused) { listenerRefused = true; }
        }
        Require(joined && listenerRefused && opened.Count == closed.Count && opened.Count == exited.Count, "Owned cleanup incomplete.");
        var ordered = arrivals.OrderBy(a => a.Ordinal).ToArray();
        var data = new
        {
            mode,
            configuredMinBackoffMilliseconds = 100,
            configuredMaxBackoffMilliseconds = 1600,
            failures = Failures,
            completeCommandRequests = commandRequests,
            statusRequests = statusTimes.Count,
            keepaliveMessagesWritten,
            statusTimestamps = statusTimes.ToArray(),
            arrivals = ordered,
            interArrivalMilliseconds = ordered.Zip(ordered.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a.Timestamp, b.Timestamp).TotalMilliseconds).ToArray(),
            stopwatchFrequency = Stopwatch.Frequency,
            runnerLogs = logs.Entries.ToArray(),
            joined,
            listenerRefused,
            openedConnections = opened.Count,
            closedConnections = closed.Count,
            exitedConnections = exited.Count,
            forbiddenEffects = effects.Calls,
            forbiddenTransfers = transfers.UnexpectedCalls,
        };
        TestContext.Out.WriteLine(JsonSerializer.Serialize(data));
        var errorAttempts = logs.Entries.Where(entry => entry.Message.Contains("Runner commands stream RPC error attempt ", StringComparison.Ordinal))
            .Select(entry => int.Parse(entry.Message.Split("error attempt ", StringSplitOptions.None)[1].Split(':')[0], System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (mode == "unavailable-before-message")
            Assert.That(errorAttempts, Is.EqualTo(new[] { 1, 2, 3 }), "A failed call with no message must retain the reconnect streak.");
        else if (mode == "keepalive-then-unavailable")
            Assert.That(errorAttempts, Is.EqualTo(new[] { 1, 1, 1 }), "A delivered message resets the reconnect streak.");
        else
            Assert.That(logs.Entries.Count(entry => entry.Message.StartsWith("Runner commands stream closed.", StringComparison.Ordinal)), Is.EqualTo(3));
    }

    private static async Task<byte[]> ReadRequestAsync(HttpContext context)
    {
        Require(context.Request.Headers["x-runner-secret"] == SyntheticSecret, "Wrong synthetic credential.");
        using var bytes = new MemoryStream();
        await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
        var frame = bytes.ToArray();
        Require(frame.Length >= 5 && frame[0] == 0 && BinaryPrimitives.ReadInt32BigEndian(frame.AsSpan(1, 4)) == frame.Length - 5,
            "Expected one complete uncompressed protobuf frame.");
        return frame;
    }

    private static async Task WriteMessageAsync(HttpContext context, IMessage message)
    {
        context.Response.ContentType = "application/grpc";
        context.Response.DeclareTrailer("grpc-status");
        var body = message.ToByteArray();
        var frame = new byte[5 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(1, 4), body.Length);
        body.CopyTo(frame, 5);
        await context.Response.Body.WriteAsync(frame, context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CaptureLogger : ILoggerProvider
    {
        public ConcurrentQueue<Log> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, Entries);
        public void Dispose() { }
        private sealed class Logger(string category, ConcurrentQueue<Log> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new(Stopwatch.GetTimestamp(), category, logLevel.ToString(), formatter(state, exception)));
        }
    }

    private sealed class ForbiddenEffects : ISessionIsolationStrategy, ILinuxCommandRunner, IRuntimeTelemetryProbe, IGpuPowerLimitCommandRunner
    {
        private int calls;
        public int Calls => calls;
        private Exception Forbidden() { Interlocked.Increment(ref calls); return new InvalidOperationException("A transport-only peer cannot perform native work."); }
        public void ValidatePrerequisites(ICollection<string> errors) => throw Forbidden();
        public Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken token) => throw Forbidden();
        public Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken token) => throw Forbidden();
        public Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken token) => throw Forbidden();
        public string CaptureRuntimeTelemetryQuery() => throw Forbidden();
        public string? ReadMemInfo() => throw Forbidden();
        public bool TryGetDiskSpace(string path, out RuntimeDiskSpaceSnapshot snapshot) { snapshot = default; throw Forbidden(); }
        public Task<GpuPowerLimitCommandResult> SetPowerLimitAsync(double targetWatts, CancellationToken token) => throw Forbidden();
    }

    private sealed class UnusedModelTransfers : IModelTransferBackend
    {
        private int unexpectedCalls;
        public int UnexpectedCalls => unexpectedCalls;
        private Exception Forbidden() { Interlocked.Increment(ref unexpectedCalls); return new InvalidOperationException("This command-stream test must not start model transfers."); }
        public string Name => "unused-command-stream-transfer";
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<ModelTransferSnapshot>> ListAsync(CancellationToken token)
            => Task.FromResult<IReadOnlyList<ModelTransferSnapshot>>([]);
        public Task SaveSessionAsync(CancellationToken token) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<IReadOnlyList<ModelTransferHandle>> CreateAsync(ModelTransferCreateRequest request, CancellationToken token)
            => throw Forbidden();
        public Task<ModelTransferSnapshot> GetStatusAsync(ModelTransferHandle handle, CancellationToken token) => throw Forbidden();
        public Task ForcePauseAsync(ModelTransferHandle handle, CancellationToken token) => throw Forbidden();
        public Task UnpauseAsync(ModelTransferHandle handle, CancellationToken token) => throw Forbidden();
        public Task ChangeUriAsync(ModelTransferHandle handle, IReadOnlyList<string> current, IReadOnlyList<string> replacement, CancellationToken token) => throw Forbidden();
        public Task MoveToFrontAsync(ModelTransferHandle handle, CancellationToken token) => throw Forbidden();
        public Task RemoveAsync(ModelTransferHandle handle, CancellationToken token) => throw Forbidden();
    }
}

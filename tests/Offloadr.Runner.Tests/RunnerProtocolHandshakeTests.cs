using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging.Abstractions;
using Offloadr.Runner.Core;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class RunnerProtocolHandshakeTests
{
    [Test]
    public void StatusAndCommandRequestsCarryThisBuildsProtocol()
    {
        var request = RunnerRegistrationLoop.BuildExecutorRequest(
            Guid.NewGuid().ToString("n"), "process-1", "1.0.0", new RunnerResources(), () => new RuntimeStatus(), ["comfyui-latest"],
            NullLogger.Instance);

        Assert.That(request.ProtocolVersion, Is.EqualTo((uint)RunnerProtocol.Current));
        Assert.That(RunnerProtocolVersion.Current, Is.GreaterThan(0u));
    }

    [Test]
    public void TheAgentRegistersAsAnExecutor_EchoingTheLaunchItWasStartedFor()
    {
        var launchId = Guid.NewGuid().ToString();
        var launched = RunnerRegistrationLoop.BuildExecutorRequest(
            Guid.NewGuid().ToString("n"), "process-1", "1.0.0", new RunnerResources(), () => new RuntimeStatus(), ["comfyui-latest"],
            NullLogger.Instance, launchId: launchId);
        var byHand = RunnerRegistrationLoop.BuildExecutorRequest(
            Guid.NewGuid().ToString("n"), "process-1", "1.0.0", new RunnerResources(), () => new RuntimeStatus(), ["comfyui-latest"],
            NullLogger.Instance);

        Assert.That(launched.Role, Is.EqualTo(RunnerRole.Executor));
        Assert.That(launched.LaunchId, Is.EqualTo(launchId), "echoed exactly as the launcher passed it");
        Assert.That(byHand.Role, Is.EqualTo(RunnerRole.Executor));
        Assert.That(byHand.LaunchId, Is.Empty);
    }

    [Test]
    public async Task EveryCallCarriesTheProtocolHeader()
    {
        var interceptor = new RunnerSecretClientInterceptor("super-secret-token");
        Metadata? observed = null;
        var method = new Method<ReportSessionEventsRequest, ReportSessionEventsResponse>(
            MethodType.Unary, "offloadr.runner.v1.RunnerControlService", "ReportSessionEvents",
            Marshallers.Create(_ => Array.Empty<byte>(), _ => new ReportSessionEventsRequest()),
            Marshallers.Create(_ => Array.Empty<byte>(), _ => new ReportSessionEventsResponse()));
        var call = interceptor.AsyncUnaryCall(
            new ReportSessionEventsRequest(),
            new ClientInterceptorContext<ReportSessionEventsRequest, ReportSessionEventsResponse>(method, "https://control-plane.example", new CallOptions()),
            (_, context) =>
            {
                observed = context.Options.Headers;
                return new AsyncUnaryCall<ReportSessionEventsResponse>(Task.FromResult(new ReportSessionEventsResponse()),
                    Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
            });
        await call.ResponseAsync;

        Assert.That(observed!.Single(entry => entry.Key == RunnerProtocolVersion.HeaderName).Value,
            Is.EqualTo(RunnerProtocolVersion.HeaderValue));
    }

    [Test]
    public async Task ArtifactAndWorkspaceCallsCarryTheProtocolHeader()
    {
        var invoker = new RecordingCallInvoker();
        var authenticated = GrpcChannelManager.CreateAuthenticatedInvoker(invoker, "super-secret-token");
        // Upload calls add the runner secret themselves; the header must still be added once.
        var callerHeaders = new Metadata { { "x-runner-secret", "super-secret-token" } };

        await new RunnerArtifactService.RunnerArtifactServiceClient(authenticated)
            .IssueUploadIdentityAsync(new RunnerArtifactServiceIssueUploadIdentityRequest(), headers: callerHeaders);
        using var read = new RunnerWorkspaceService.RunnerWorkspaceServiceClient(authenticated)
            .ReadWorkspaceFile(new RunnerWorkspaceServiceReadWorkspaceFileRequest(), headers: callerHeaders);

        Assert.That(invoker.Headers, Has.Count.EqualTo(2));
        foreach (var headers in invoker.Headers)
        {
            Assert.That(headers.Single(entry => entry.Key == RunnerProtocolVersion.HeaderName).Value, Is.EqualTo(RunnerProtocolVersion.HeaderValue));
            Assert.That(headers.Count(entry => entry.Key == "x-runner-secret"), Is.EqualTo(1));
        }
    }

    private sealed class EmptyStream<T> : IAsyncStreamReader<T>
    {
        public T Current => throw new InvalidOperationException();

        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RecordingCallInvoker : CallInvoker
    {
        public List<Metadata> Headers { get; } = [];

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Headers.Add(options.Headers ?? []);
            var response = (TResponse)Activator.CreateInstance(typeof(TResponse))!;
            return new AsyncUnaryCall<TResponse>(Task.FromResult(response), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
            => throw new NotSupportedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Headers.Add(options.Headers ?? []);
            return new AsyncServerStreamingCall<TResponse>(new EmptyStream<TResponse>(), Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options)
            => throw new NotSupportedException();
    }

    [TestCase(RegistrationRejection.RunnerOutdated)]
    [TestCase(RegistrationRejection.ServerOutdated)]
    [TestCase(RegistrationRejection.LauncherSuperseded)]
    [TestCase(RegistrationRejection.ExecutorFenced)]
    public async Task AFinalRejectionStopsTheStatusLoopInsteadOfRetrying(RegistrationRejection rejection)
    {
        var calls = 0;
        RegisterRunnerResponse? reported = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await RunLoopAsync(_ =>
        {
            calls++;
            return new RegisterRunnerResponse
            {
                Accepted = false,
                Message = "Update the runner.",
                Rejection = rejection,
                ServerProtocolVersion = RunnerProtocolVersion.Current + 1,
            };
        }, timeout.Token, response => reported = response);

        Assert.That(timeout.IsCancellationRequested, Is.False, "the loop returns by itself");
        Assert.That(reported?.Rejection, Is.EqualTo(rejection));
        Assert.That(calls, Is.EqualTo(1));
        Assert.That(RunnerProtocolVersion.IsProtocolRejection(reported!), Is.EqualTo(rejection is RegistrationRejection.RunnerOutdated or RegistrationRejection.ServerOutdated),
            "only an outdated protocol means 'update'; the agent exits with a different code otherwise");
    }

    [Test]
    public async Task OtherRefusalsAreStillRetried()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var calls = 0;
        var rejected = false;

        await RunLoopAsync(_ =>
        {
            if (++calls == 3) stop.Cancel();
            return new RegisterRunnerResponse { Accepted = false, Message = "Account access is not available." };
        }, stop.Token, _ => rejected = true);

        Assert.That(calls, Is.GreaterThanOrEqualTo(3));
        Assert.That(rejected, Is.False);
    }

    [Test]
    public async Task TheFirstAcceptedRegistrationIsAnnounced_AndRefusalsBeforeItAreNot()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var initial = new TaskCompletionSource();
        var calls = 0;
        var announcedAt = 0;
        var requests = new List<RegisterRunnerRequest>();

        await RunLoopAsync(request =>
        {
            requests.Add(request);
            calls++;
            if (calls == 3)
            {
                announcedAt = initial.Task.IsCompleted ? -1 : 3;
                return new RegisterRunnerResponse { Accepted = true, RefreshSeconds = 1 };
            }

            if (calls == 4)
            {
                stop.Cancel();
            }

            return new RegisterRunnerResponse { Accepted = calls > 3, Message = "Try again." };
        }, stop.Token, initialStatusApplied: initial);

        Assert.That(announcedAt, Is.EqualTo(3), "nothing was announced before the first accepted registration");
        Assert.That(initial.Task.IsCompletedSuccessfully, Is.True);
        Assert.That(requests, Has.Count.EqualTo(4), "each heartbeat is built afresh");
        Assert.That(requests.Distinct().Count(), Is.EqualTo(4));
    }

    [Test]
    public async Task AFailingRequestBuilderIsRetried_NotFatal()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var builds = 0;
        var registered = 0;

        await RunnerRegistrationLoop.RunAsync(
            (_, _) =>
            {
                registered++;
                stop.Cancel();
                return Task.FromResult(new RegisterRunnerResponse { Accepted = true });
            },
            () => ++builds < 3 ? throw new InvalidOperationException("probe failed") : NewRequest(),
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(5),
            new TaskCompletionSource(),
            NullLogger.Instance,
            stop.Token);

        Assert.That(builds, Is.EqualTo(3));
        Assert.That(registered, Is.EqualTo(1));
    }

    private static RegisterRunnerRequest NewRequest()
        => RunnerRegistrationLoop.BuildExecutorRequest(
            Guid.NewGuid().ToString("n"), "process-1", "1.0.0", new RunnerResources(), () => new RuntimeStatus(), ["comfyui-latest"],
            NullLogger.Instance);

    private static Task RunLoopAsync(
        Func<RegisterRunnerRequest, RegisterRunnerResponse> answer,
        CancellationToken shutdown,
        Action<RegisterRunnerResponse>? onProtocolRejected = null,
        TaskCompletionSource? initialStatusApplied = null)
        => RunnerRegistrationLoop.RunAsync(
            (request, _) => Task.FromResult(answer(request)),
            NewRequest,
            TimeSpan.FromMilliseconds(1),
            TimeSpan.FromMilliseconds(5),
            initialStatusApplied ?? new TaskCompletionSource(),
            NullLogger.Instance,
            shutdown,
            onProtocolRejected);
}

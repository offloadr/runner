using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Moq;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public class GrpcRuntimeActivitySinkTests
{
    [Test]
    public async Task EachAttemptUsesFreshMetadataAndFiniteDeadline()
    {
        var requests = new List<ReportRuntimeActivityRequest>();
        var options = new List<CallOptions>();
        var disposed = 0;
        var invoker = Invoker((request, callOptions) =>
        {
            requests.Add(request);
            options.Add(callOptions);
            return new AsyncUnaryCall<ReportRuntimeActivityResponse>(
                Task.FromResult(new ReportRuntimeActivityResponse
                {
                    Outcome = RuntimeActivityOutcome.Applied,
                    RuntimeStartedUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
                }), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => disposed++);
        });
        var runnerId = Guid.NewGuid().ToString();
        var sink = new GrpcRuntimeActivitySink(new(invoker.Object), runnerId, "original-process");
        var observation = RuntimeActivityObservation.Capture("session-1", new("33333333-3333-4333-8333-333333333333", 23),
            new() { MemoryWorkspace = true, EncryptedUploads = true, EncryptedReadsRequired = true });
        using var stop = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        await sink.ReportAsync(observation, stop.Token);
        requests[0].SessionId = "changed-by-transport";
        requests[0].Sealing.MemoryWorkspace = false;
        await sink.ReportAsync(observation, stop.Token);
        Assert.Multiple(() =>
        {
            Assert.That(requests, Has.Count.EqualTo(2));
            Assert.That(requests[1], Is.Not.SameAs(requests[0]));
            Assert.That(requests[1].SessionId, Is.EqualTo(observation.SessionId));
            Assert.That(requests[1].RunnerId, Is.EqualTo(Guid.Parse(runnerId).ToString("n")));
            Assert.That(requests[1].InstanceId, Is.EqualTo("original-process"));
            Assert.That(requests[1].Runtime, Is.EqualTo(new RuntimeRef { RuntimeId = "33333333333343338333333333333333", RuntimeOrder = 23 }));
            Assert.That(requests[1].Sealing.MemoryWorkspace, Is.True);
            Assert.That(requests[1].Sealing.EncryptedUploads, Is.True);
            Assert.That(requests[1].Sealing.EncryptedReadsRequired, Is.True);
            Assert.That(options.All(option => option.CancellationToken == stop.Token), Is.True);
            Assert.That(options.All(option => option.Deadline is { } deadline && deadline > started &&
                deadline <= DateTime.UtcNow.Add(RuntimeActivityScope.AttemptTimeout)), Is.True);
            Assert.That(disposed, Is.EqualTo(2));
        });
    }

    [TestCase(RuntimeActivityOutcome.Applied)]
    [TestCase(RuntimeActivityOutcome.AlreadyAppliedExactly)]
    public void SuccessWithoutAcceptedTimeIsAProtocolFailure(RuntimeActivityOutcome outcome)
    {
        var invoker = Invoker((_, _) => Reply(new() { Outcome = outcome }));
        var sink = new GrpcRuntimeActivitySink(new(invoker.Object), Guid.NewGuid().ToString(), "process");
        Assert.ThrowsAsync<InvalidDataException>(() => sink.ReportAsync(
            new("session-1", new(Guid.NewGuid().ToString("n"), 1), false, false, false), default));
    }

    [Test]
    public void RequiredFactSchemaHasNoDiagnosticBodyOrClientClock()
    {
        Assert.That(ReportRuntimeActivityRequest.Descriptor.Fields.InDeclarationOrder().Select(field => field.Name), Is.EqualTo(new[]
        {
            "runner_id", "session_id", "instance_id", "sealing", "runtime"
        }));
    }

    private static AsyncUnaryCall<ReportRuntimeActivityResponse> Reply(ReportRuntimeActivityResponse response)
        => new(Task.FromResult(response), Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static Mock<CallInvoker> Invoker(Func<ReportRuntimeActivityRequest, CallOptions, AsyncUnaryCall<ReportRuntimeActivityResponse>> call)
    {
        var invoker = new Mock<CallInvoker>(MockBehavior.Strict);
        invoker.Setup(peer => peer.AsyncUnaryCall(
                It.IsAny<Method<ReportRuntimeActivityRequest, ReportRuntimeActivityResponse>>(),
                It.IsAny<string?>(), It.IsAny<CallOptions>(), It.IsAny<ReportRuntimeActivityRequest>()))
            .Returns((Method<ReportRuntimeActivityRequest, ReportRuntimeActivityResponse> _, string? _, CallOptions options, ReportRuntimeActivityRequest request) => call(request, options));
        return invoker;
    }
}

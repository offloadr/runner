namespace Offloadr.Runner.Tests;

using Google.Protobuf;
using Offloadr.EditorRuntime.V1;
using Offloadr.Runner.V1;

public partial class ServiceClientManagerTests
{
    [Test]
    public async Task TransientCommand_CapturesTheAdmittedIntentBeforeAsynchronousExecution()
    {
        var harness = new TransientRequestHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeResult = release.Task;
        var delivered = harness.Request("original-delivery");
        var original = delivered.Clone();
        var execution = harness.DispatchAsync(delivered);
        try
        {
            delivered.RequestId = "changed-request";
            delivered.SessionId = "changed-session";
            delivered.DeliveryId = "changed-delivery";
        }
        finally
        {
            release.TrySetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }

        original.DeliveryId = "matching-delivery";
        original.Attempt++;
        await harness.DispatchAsync(original);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.EqualTo(1));
            Assert.That(harness.Acknowledgements.Select(ack => ack.RequestId), Is.All.EqualTo(original.RequestId));
            Assert.That(harness.Acknowledgements.Select(ack => ack.SessionId), Is.All.EqualTo(original.SessionId));
            Assert.That(harness.Acknowledgements.Select(ack => ack.DeliveryId), Is.EqualTo(new[] { "original-delivery", "matching-delivery" }));
            Assert.That(harness.Deliveries.InFlightCount, Is.Zero);
            Assert.That(harness.Cancellation.Count, Is.Zero);
        });
    }

    private static IEnumerable<TestCaseData> ConflictingTransientCommands()
    {
        var changes = new (string Name, Action<RelayEditorRuntimeRequestCommand> Change)[]
        {
            ("runner", command => command.RunnerId = "99999999999949998999999999999999"),
            ("body", command => command.Body = ByteString.CopyFromUtf8("different body")),
            ("path", command => command.Path = "interrupt"),
            ("method", command => command.Method = "POST"),
            ("query", command => command.Query = "clear=true"),
            ("content type", command => command.ContentType = "application/json"),
            ("client", command => command.ClientId = "other-client"),
            ("client message", command => command.ClientMessage = ByteString.CopyFromUtf8("other message")),
            ("editor", command => command.EditorSid = "other-editor"),
            ("session", command => command.SessionId = "33333333333343338333333333333333"),
            ("generation", command => command.LifecycleGeneration++),
            ("epoch", command => command.RuntimeEpoch++),
            ("instance", command => command.RuntimeInstanceId = InstanceB),
            ("kind", command => command.Kind = EditorRuntimeRequestKind.Control),
            ("urgency", command => command.Urgent = true),
            ("deadline", command => command.ExpiresUtc.Seconds++),
        };

        foreach (var completed in new[] { false, true })
        {
            foreach (var (name, change) in changes)
            {
                yield return new TestCaseData(completed, change)
                    .SetName($"ConflictingTransientCommand_{(completed ? "completed" : "inflight")}_{name.Replace(' ', '_')}");
            }
        }
    }

    [TestCaseSource(nameof(ConflictingTransientCommands))]
    public async Task ConflictingTransientCommand_IsRejectedWithoutJoiningOrReplaying(
        bool completed,
        Action<RelayEditorRuntimeRequestCommand> change)
    {
        var harness = new TransientRequestHarness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.BeforeResult = completed ? Task.CompletedTask : release.Task;
        var original = harness.Request("original-delivery");
        var first = harness.DispatchAsync(original);
        if (completed)
        {
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }

        try
        {
            var conflict = original.Clone();
            conflict.DeliveryId = "conflicting-delivery";
            conflict.Attempt++;
            change(conflict);
            await harness.DispatchAsync(conflict).WaitAsync(TimeSpan.FromSeconds(5));

            var rejection = harness.Acknowledgements.SingleOrDefault(ack => ack.DeliveryId == conflict.DeliveryId);
            Assert.That(rejection, Is.Not.Null, "A conflicting delivery must not join the original execution.");
            Assert.Multiple(() =>
            {
                Assert.That(rejection!.StatusCode, Is.EqualTo(409));
                Assert.That(rejection.TransportSucceeded, Is.False);
                Assert.That(rejection.Body.IsEmpty, Is.True, "Never return another command's response body.");
                Assert.That(rejection.RunnerId, Is.EqualTo(conflict.RunnerId));
                Assert.That(rejection.SessionId, Is.EqualTo(conflict.SessionId));
                Assert.That(rejection.RuntimeInstanceId, Is.EqualTo(conflict.RuntimeInstanceId));
                Assert.That(harness.Executions, Is.EqualTo(1));
            });
        }
        finally
        {
            release.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }

        var matching = original.Clone();
        matching.DeliveryId = "matching-delivery";
        matching.Attempt += 2;
        await harness.DispatchAsync(matching).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(harness.Executions, Is.EqualTo(1));
            Assert.That(harness.Acknowledgements.Count(ack => ack.DeliveryId == "conflicting-delivery"), Is.EqualTo(1));
            Assert.That(harness.Acknowledgements.Single(ack => ack.DeliveryId == "matching-delivery").Body,
                Is.EqualTo(harness.Acknowledgements.Single(ack => ack.DeliveryId == "original-delivery").Body));
            Assert.That(harness.Acknowledgements.Single(ack => ack.DeliveryId == "matching-delivery").StatusCode, Is.EqualTo(200));
            Assert.That(harness.Deliveries.InFlightCount, Is.Zero);
            Assert.That(harness.Cancellation.Count, Is.Zero);
        });
    }
}

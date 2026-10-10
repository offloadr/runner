using Google.Protobuf.WellKnownTypes;
using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class PromptExecutionPermissionTests
{
    [Test]
    public async Task DuplicateDeliveryAfterLostAcknowledgementOnlyObservesOriginalWork()
    {
        var command = Command();
        var grants = 0;
        var invocations = 0;
        var observations = 0;
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        Task<GrantPromptExecutionResponse> Grant(GrantPromptExecutionRequest request, CancellationToken token)
        {
            Assert.That(request.Target, Is.EqualTo(command.Target));
            Assert.That(request.CommandId, Is.EqualTo(command.CommandId));
            return Task.FromResult(Permission(command, ++grants == 1));
        }
        for (var delivery = 0; delivery < 2; delivery++)
            await Execute(command, Grant, (_, _) => { invocations++; return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, [0xff, 0x00, 0x80], "observed")); },
                (_, _) => { observations++; return Task.CompletedTask; },
                (ack, _) => { acknowledgements.Add(ack.Clone()); throw new IOException("ack response lost"); });
        Assert.That(invocations, Is.EqualTo(1));
        Assert.That(observations, Is.EqualTo(1));
        Assert.That(acknowledgements, Has.Count.EqualTo(1));
        Assert.That(acknowledgements[0].Target, Is.EqualTo(command.Target));
        Assert.That(acknowledgements[0].Result.NativeResponse.Body.ToByteArray(), Is.EqualTo(new byte[] { 0xff, 0x00, 0x80 }));
    }

    [Test]
    public async Task RecoveryDeliveryNeverAsksForAnotherExecutionPermission()
    {
        var command = Command();
        command.RecoveryOnly = true;
        var observations = 0;
        await Execute(command,
            (_, _) => throw new AssertionException("Recovery must not ask for execution"),
            (_, _) => throw new AssertionException("Recovery must not invoke a generation"),
            (captured, _) => { Assert.That(captured.Target, Is.EqualTo(command.Target)); observations++; return Task.CompletedTask; },
            (_, _) => throw new AssertionException("Observation cannot invent an original acknowledgement"));
        Assert.That(observations, Is.EqualTo(1));
    }

    [TestCase("generation")]
    [TestCase("runner")]
    [TestCase("command")]
    public void MissingOrMismatchedIdentityCannotReachPermission(string field)
    {
        var command = Command();
        if (field == "generation") command.Target.GpuGeneration = 0;
        else if (field == "runner") command.Target.RunnerId = Guid.NewGuid().ToString("n");
        else command.CommandId = "";
        Assert.ThrowsAsync<ArgumentException>(() => Execute(command,
            (_, _) => throw new AssertionException("Invalid identity reached permission"),
            (_, _) => throw new AssertionException("Invalid identity invoked the runtime")));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void WrongReceiptOrMissingCommittedPermissionCannotInvokeRuntime(bool differentTarget)
    {
        var command = Command();
        var permission = Permission(command, true);
        if (differentTarget) permission.Submission.Target.RuntimeEpoch++;
        else permission.Submission.ExecutionPermissionUtc = null;
        Assert.ThrowsAsync<InvalidOperationException>(() => Execute(command, (_, _) => Task.FromResult(permission),
            (_, _) => throw new AssertionException("Mismatched permission invoked the runtime")));
    }

    [Test]
    public async Task LostNativeResultRemainsUnknownWithoutInventingHttpFailure()
    {
        var command = Command();
        AcknowledgePromptResultRequest? observed = null;
        await Execute(command, (_, _) => Task.FromResult(Permission(command, true)),
            (_, _) => throw new IOException("runtime connection lost after write"),
            acknowledge: (ack, _) => { observed = ack; return Task.CompletedTask; });
        Assert.That(observed!.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.Unknown));
        Assert.That(observed.Result.NativeResponse, Is.Null);
        Assert.That(observed.Result.Message, Does.Not.Contain("connection lost after write"));
    }

    [Test]
    public async Task CancellationAfterGrantBeforeNativeCallIsProvenNotExecuted()
    {
        var command = Command();
        using var cancellation = new CancellationTokenSource();
        AcknowledgePromptResultRequest? observed = null;
        await Execute(command, (_, _) => { cancellation.Cancel(); return Task.FromResult(Permission(command, true)); },
            (_, _) => throw new AssertionException("Cancelled preparation invoked the runtime"),
            acknowledge: (ack, token) => { Assert.That(token.IsCancellationRequested, Is.False); observed = ack; return Task.CompletedTask; },
            cancellationToken: cancellation.Token);
        Assert.That(observed!.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NotExecuted));
        Assert.That(observed.Result.NativeResponse, Is.Null);
    }

    [Test]
    public async Task MissingArtifactUploaderAfterPermissionAcknowledgesNotExecuted()
    {
        var command = Command();
        AcknowledgePromptResultRequest? acknowledged = null;
        await Execute(command, (_, _) => Task.FromResult(Permission(command, true)),
            (_, _) => throw new AssertionException("Missing uploader invoked the runtime"),
            acknowledge: (ack, _) => { acknowledged = ack.Clone(); return Task.CompletedTask; },
            captureRefresh: _ => throw new InvalidOperationException("The captured artifact uploader is not active."));
        Assert.That(acknowledged!.CommandId, Is.EqualTo(command.CommandId));
        Assert.That(acknowledged.Target, Is.EqualTo(command.Target));
        Assert.That(acknowledged.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NotExecuted));
        Assert.That(acknowledged.Result.DiagnosticCode, Is.EqualTo("prompt_preparation_incomplete"));
    }

    [Test]
    public async Task FailedLocalForgePreparationBeforeNativeIoAcknowledgesNotExecuted()
    {
        var command = Command();
        command.EditorRuntimeKind = "forge-neo";
        command.HttpPath = "queue/join";
        AcknowledgePromptResultRequest? acknowledged = null;
        await Execute(command, (_, _) => Task.FromResult(Permission(command, true)),
            (_, _) => throw new System.Text.Json.JsonException("local Forge request preparation failed"),
            acknowledge: (ack, _) => { acknowledged = ack.Clone(); return Task.CompletedTask; },
            nativeRequestAttempted: false);
        Assert.That(acknowledged!.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NotExecuted));
        Assert.That(acknowledged.Result.DiagnosticCode, Is.EqualTo("prompt_preparation_incomplete"));
    }

    [Test]
    public async Task NativeErrorStatusIsRetainedAsObservedResponse()
    {
        var command = Command();
        AcknowledgePromptResultRequest? observed = null;
        await Execute(command, (_, _) => Task.FromResult(Permission(command, true)),
            (_, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(false, 503, [1, 2, 3], "not generation evidence", "application/octet-stream")),
            acknowledge: (ack, _) => { observed = ack; return Task.CompletedTask; });
        Assert.That(observed!.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NativeResponse));
        Assert.That(observed.Result.NativeResponse.StatusCode, Is.EqualTo(503));
        Assert.That(observed.Result.NativeResponse.Body.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(observed.Result.NativeResponse.ContentType, Is.EqualTo("application/octet-stream"));
    }

    [Test]
    public async Task OmittedNativeBodyKeepsExactStatusAndExplicitDiagnostic()
    {
        var command = Command();
        AcknowledgePromptResultRequest? observed = null;
        await Execute(command, (_, _) => Task.FromResult(Permission(command, true)),
            (_, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, [], "Prompt submitted", "application/json", BodyOmitted: true)),
            acknowledge: (ack, _) => { observed = ack; return Task.CompletedTask; });
        Assert.That(observed!.Result.Outcome, Is.EqualTo(PromptInvocationOutcome.NativeResponse));
        Assert.That(observed.Result.NativeResponse.StatusCode, Is.EqualTo(200));
        Assert.That(observed.Result.NativeResponse.Body.IsEmpty, Is.True);
        Assert.That(observed.Result.DiagnosticCode, Is.EqualTo(PromptProtocolCodes.NativeResponseBodyOmitted));
    }

    [Test]
    public void RetiredInterruptDoesNotCancelPreparationForAReplacementRuntime()
    {
        var command = Command();
        var interrupt = new RelayEditorRuntimeRequestCommand
        {
            SessionId = command.SessionId,
            LifecycleGeneration = command.Target.GpuGeneration,
            RuntimeEpoch = command.Target.RuntimeEpoch,
            RuntimeInstanceId = command.Target.RuntimeInstanceId,
        };
        Assert.That(ServiceClientManager.TargetsPromptRuntime(interrupt, command), Is.True);
        command.Target.RuntimeEpoch++;
        command.Target.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        Assert.That(ServiceClientManager.TargetsPromptRuntime(interrupt, command), Is.False);
    }

    private static SubmitPromptCommand Command() => new()
    {
        CommandId = Guid.NewGuid().ToString("n"),
        SubmissionId = Guid.NewGuid().ToString("n"),
        SessionId = "22222222222242228222222222222222",
        EditorSid = "editor-1",
        Owner = "alice",
        EditorRuntimeKind = "comfyui",
        HttpMethod = "POST",
        HttpPath = "api/prompt",
        PromptJson = "{}",
        Target = new()
        {
            EditorLifecycleId = "cpu-1",
            EditorGeneration = 1,
            CpuRuntimeGeneration = 1,
            CpuRuntimeInstanceId = "33333333333343338333333333333333",
            GpuLifecycleId = "gpu-1",
            GpuGeneration = 1,
            AllocationId = "allocation-1",
            RuntimeEpoch = 1,
            RuntimeInstanceId = "44444444444444448444444444444444",
            RunnerId = "11111111111141118111111111111111",
            RunnerSessionId = "22222222222242228222222222222222",
        },
    };

    private static GrantPromptExecutionResponse Permission(SubmitPromptCommand command, bool mayExecute) => new()
    {
        MayExecute = mayExecute,
        Submission = new()
        {
            SubmissionId = command.SubmissionId,
            Target = command.Target.Clone(),
            ExecutionPermissionUtc = Timestamp.FromDateTime(DateTime.UtcNow)
        },
    };

    private static Task Execute(SubmitPromptCommand command,
        Func<GrantPromptExecutionRequest, CancellationToken, Task<GrantPromptExecutionResponse>> grant,
        Func<SubmitPromptCommand, CancellationToken, Task<SessionProcessManager.PromptSubmissionResult>> invoke,
        Func<SubmitPromptCommand, CancellationToken, Task>? observe = null,
        Func<AcknowledgePromptResultRequest, CancellationToken, Task>? acknowledge = null,
        CancellationToken cancellationToken = default,
        Func<string, Func<Task>>? captureRefresh = null,
        bool nativeRequestAttempted = true)
        => ServiceClientManager.HandleSubmitEditorActionAsync(
            acknowledge ?? ((_, _) => Task.CompletedTask), (_, _) => Task.CompletedTask,
            command, "11111111111141118111111111111111", grant, (_, _) => Task.CompletedTask,
            observe ?? ((_, _) => throw new AssertionException("Unexpected recovery")),
            (_, _, _, _) => Task.CompletedTask, (_, _, _) => { }, _ => { },
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _, _) => Task.CompletedTask,
            captureRefresh ?? (_ => () => Task.CompletedTask), (_, _) => { }, (_, _, _) => Task.CompletedTask,
            (captured, _, onNativeRequestAttempt, token) =>
            {
                if (nativeRequestAttempted) onNativeRequestAttempt();
                return invoke(captured, token);
            }, (_, _, _, _) => { }, cancellationToken);
}

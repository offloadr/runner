using Grpc.Core;
using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

public sealed class PromptExecutionPermissionTests
{
    private const string RunnerId = "11111111111141118111111111111111";
    private const string InstanceId = "runner-process-1";

    [TestCase(PromptGrantOutcome.AlreadyGranted)]
    [TestCase(PromptGrantOutcome.MissingOrExpired)]
    [TestCase(PromptGrantOutcome.TargetNotCurrent)]
    [TestCase(PromptGrantOutcome.AccountUnavailable)]
    [TestCase(PromptGrantOutcome.Unspecified)]
    public async Task AHandoffThatIsNotGrantedRunsNothingAndIsNotAcknowledged(PromptGrantOutcome outcome)
    {
        var command = Command();
        var grants = 0;
        var granted = false;
        await Execute(command,
            (_, _) => { grants++; return Task.FromResult(new GrantPromptExecutionResponse { Outcome = outcome }); },
            (_, _) => throw new AssertionException("A handoff that was not granted invoked the runtime"),
            acknowledge: (_, _) => throw new AssertionException("A handoff that was not granted was acknowledged"),
            captureRefresh: _ => throw new AssertionException("A handoff that was not granted prepared artifacts"),
            prepareDownloads: (_, _, _) => throw new AssertionException("A handoff that was not granted prepared downloads"),
            ensureBridge: (_, _, _, _) => throw new AssertionException("A handoff that was not granted opened a bridge"),
            onGranted: () => granted = true);
        Assert.Multiple(() =>
        {
            Assert.That(grants, Is.EqualTo(1), "A refused grant is not retried.");
            Assert.That(granted, Is.False);
        });
    }

    [TestCase(StatusCode.FailedPrecondition)]
    [TestCase(StatusCode.PermissionDenied)]
    [TestCase(StatusCode.InvalidArgument)]
    public async Task ANonRetryableGrantErrorDropsTheDeliveryWithoutAcknowledgement(StatusCode status)
    {
        var command = Command();
        var grants = 0;
        await Execute(command,
            (_, _) => { grants++; throw new RpcException(new(status, "refused")); },
            (_, _) => throw new AssertionException("A refused grant invoked the runtime"),
            acknowledge: (_, _) => throw new AssertionException("A refused grant was acknowledged"));
        Assert.That(grants, Is.EqualTo(1));
    }

    [Test]
    public async Task AGrantedNativeResponseIsAcknowledgedExactlyOnce()
    {
        var command = Command();
        var grantRequests = new List<GrantPromptExecutionRequest>();
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        var invocations = 0;
        var granted = 0;
        await Execute(command,
            (request, _) => { grantRequests.Add(request.Clone()); return Granted(); },
            (_, _) =>
            {
                invocations++;
                return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, [0xff, 0x00, 0x80], "observed", "application/json"));
            },
            acknowledge: (ack, _) => { acknowledgements.Add(ack.Clone()); return Task.CompletedTask; },
            onGranted: () => granted++);
        Assert.Multiple(() =>
        {
            Assert.That(invocations, Is.EqualTo(1));
            Assert.That(granted, Is.EqualTo(1));
            Assert.That(grantRequests.Single(), Is.EqualTo(new GrantPromptExecutionRequest
            {
                RunnerId = RunnerId,
                SessionId = command.SessionId,
                HandoffId = command.HandoffId,
                InstanceId = InstanceId,
                LifecycleGeneration = command.LifecycleGeneration,
                RuntimeEpoch = command.RuntimeEpoch,
                RuntimeInstanceId = command.RuntimeInstanceId,
            }));
            var ack = acknowledgements.Single();
            Assert.That(ack.RunnerId, Is.EqualTo(RunnerId));
            Assert.That(ack.SessionId, Is.EqualTo(command.SessionId));
            Assert.That(ack.HandoffId, Is.EqualTo(command.HandoffId));
            Assert.That(ack.InstanceId, Is.EqualTo(InstanceId));
            Assert.That(ack.Outcome, Is.EqualTo(PromptResultOutcome.NativeResponse));
            Assert.That(ack.StatusCode, Is.EqualTo(200));
            Assert.That(ack.Body.ToByteArray(), Is.EqualTo(new byte[] { 0xff, 0x00, 0x80 }));
            Assert.That(ack.ContentType, Is.EqualTo("application/json"));
            Assert.That(ack.BodyOmitted, Is.False);
            Assert.That(ack.DiagnosticCode, Is.Empty);
        });
    }

    [TestCase(StatusCode.Unavailable)]
    [TestCase(StatusCode.DeadlineExceeded)]
    public async Task ALostGrantReplyIsRetriedAndTheHandoffRunsOnce(StatusCode lostReply)
    {
        var command = Command();
        var grantRequests = new List<GrantPromptExecutionRequest>();
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        var invocations = 0;
        await Execute(command,
            (request, _) =>
            {
                grantRequests.Add(request.Clone());
                // The platform committed the first grant, but its reply was lost.
                if (grantRequests.Count == 1) throw new RpcException(new(lostReply, "grant reply lost"));
                return Granted();
            },
            (_, _) =>
            {
                invocations++;
                return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, "{}"u8.ToArray(), "observed"));
            },
            acknowledge: (ack, _) => { acknowledgements.Add(ack.Clone()); return Task.CompletedTask; });
        Assert.Multiple(() =>
        {
            Assert.That(grantRequests, Has.Count.EqualTo(2));
            Assert.That(grantRequests[1], Is.EqualTo(grantRequests[0]), "The retry is the same grant.");
            Assert.That(invocations, Is.EqualTo(1));
            Assert.That(acknowledgements, Has.Count.EqualTo(1));
            Assert.That(acknowledgements[0].Outcome, Is.EqualTo(PromptResultOutcome.NativeResponse));
        });
    }

    [Test]
    public async Task AHandoffForARuntimeThatIsNotCurrentIsNeverGranted()
    {
        var command = Command();
        SubmitPromptCommand? checkedCommand = null;
        await Execute(command,
            (_, _) => throw new AssertionException("A handoff for another runtime asked for a grant"),
            (_, _) => throw new AssertionException("A handoff for another runtime invoked the runtime"),
            acknowledge: (_, _) => throw new AssertionException("A handoff for another runtime was acknowledged"),
            isRuntimeCurrent: candidate => { checkedCommand = candidate; return false; });
        Assert.That(checkedCommand, Is.SameAs(command));
    }

    [TestCase("handoff")]
    [TestCase("session")]
    [TestCase("generation")]
    [TestCase("epoch")]
    [TestCase("instance")]
    public void AMissingOrMalformedIdentityCannotReachTheGrant(string field)
    {
        var command = Command();
        if (field == "handoff") command.HandoffId = "";
        else if (field == "session") command.SessionId = "not-a-session";
        else if (field == "generation") command.LifecycleGeneration = 0;
        else if (field == "epoch") command.RuntimeEpoch = 0;
        else command.RuntimeInstanceId = Guid.Empty.ToString("n");
        Assert.ThrowsAsync<ArgumentException>(() => Execute(command,
            (_, _) => throw new AssertionException("Invalid identity reached the grant"),
            (_, _) => throw new AssertionException("Invalid identity invoked the runtime")));
    }

    [Test]
    public async Task ALostNativeResultIsAcknowledgedUnknown()
    {
        var command = Command();
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        await Execute(command, (_, _) => Granted(),
            (_, _) => throw new IOException("runtime connection lost after write"),
            acknowledge: (ack, _) => { acknowledgements.Add(ack.Clone()); return Task.CompletedTask; });
        var observed = acknowledgements.Single();
        Assert.Multiple(() =>
        {
            Assert.That(observed.Outcome, Is.EqualTo(PromptResultOutcome.Unknown));
            Assert.That(observed.DiagnosticCode, Is.EqualTo("prompt_native_result_unknown"));
            Assert.That(observed.StatusCode, Is.Zero);
            Assert.That(observed.Body.IsEmpty, Is.True);
        });
    }

    [Test]
    public async Task CancellationAfterTheGrantBeforeTheNativeCallIsAcknowledgedNotExecuted()
    {
        var command = Command();
        using var cancellation = new CancellationTokenSource();
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        await Execute(command, (_, _) => { cancellation.Cancel(); return Granted(); },
            (_, _) => throw new AssertionException("Cancelled preparation invoked the runtime"),
            acknowledge: (ack, token) => { Assert.That(token.IsCancellationRequested, Is.False); acknowledgements.Add(ack.Clone()); return Task.CompletedTask; },
            cancellationToken: cancellation.Token);
        Assert.Multiple(() =>
        {
            Assert.That(acknowledgements.Single().Outcome, Is.EqualTo(PromptResultOutcome.NotExecuted));
            Assert.That(acknowledgements.Single().DiagnosticCode, Is.EqualTo("prompt_preparation_incomplete"));
        });
    }

    [Test]
    public async Task CancellationBeforeTheGrantIsAskedAsksNothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Execute(Command(),
            (_, _) => throw new AssertionException("A cancelled handoff asked for a grant"),
            (_, _) => throw new AssertionException("A cancelled handoff invoked the runtime"),
            acknowledge: (_, _) => throw new AssertionException("Nothing was granted, so nothing is acknowledged"),
            cancellationToken: cancellation.Token);
    }

    [Test]
    public async Task AGrantAnsweredAfterAnInterruptIsRememberedAndAcknowledgedNotExecuted()
    {
        // The platform recorded GRANTED while the interrupt arrived. The answer is still
        // read, so the grant ends in an acknowledgement and is never run.
        var command = Command();
        using var cancellation = new CancellationTokenSource();
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        var granted = 0;
        await Execute(command,
            async (_, token) =>
            {
                cancellation.Cancel();
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                Assert.That(token.IsCancellationRequested, Is.False, "The interrupt abandoned a sent grant.");
                return await Granted();
            },
            (_, _) => throw new AssertionException("An interrupted handoff invoked the runtime"),
            acknowledge: (ack, _) => { acknowledgements.Add(ack.Clone()); return Task.CompletedTask; },
            cancellationToken: cancellation.Token,
            onGranted: () => granted++);
        Assert.Multiple(() =>
        {
            Assert.That(granted, Is.EqualTo(1));
            Assert.That(acknowledgements.Single().Outcome, Is.EqualTo(PromptResultOutcome.NotExecuted));
            Assert.That(acknowledgements.Single().DiagnosticCode, Is.EqualTo("prompt_preparation_incomplete"));
        });
    }

    [Test]
    public async Task AGrantThatNeverSettlesAfterAnInterruptIsRememberedSoItNeverRuns()
    {
        var command = Command();
        using var cancellation = new CancellationTokenSource();
        var grants = 0;
        var granted = 0;
        await Execute(command,
            async (_, token) =>
            {
                grants++;
                cancellation.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertionException("The unsettled grant returned.");
            },
            (_, _) => throw new AssertionException("An interrupted handoff invoked the runtime"),
            acknowledge: (_, _) => throw new AssertionException("No grant was received, so nothing is acknowledged"),
            cancellationToken: cancellation.Token,
            onGranted: () => granted++,
            grantSettleTimeout: TimeSpan.FromMilliseconds(100)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(grants, Is.EqualTo(1));
        Assert.That(granted, Is.EqualTo(1), "The platform may hold this grant, so a redelivery must not run it.");
    }

    [Test]
    public async Task HostShutdownWhileWaitingForTheGrantRemembersNothing()
    {
        using var shutdown = new CancellationTokenSource();
        var granted = 0;
        await Execute(Command(),
            async (_, token) =>
            {
                shutdown.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                throw new AssertionException("The grant outlived the host.");
            },
            (_, _) => throw new AssertionException("A shut-down handoff invoked the runtime"),
            acknowledge: (_, _) => throw new AssertionException("Nothing was granted, so nothing is acknowledged"),
            onGranted: () => granted++,
            hostShutdown: shutdown.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(granted, Is.Zero);
    }

    [TestCase("answered")]
    [TestCase("unsettled")]
    public async Task AHandoffInterruptedDuringItsGrantIsNotRunByARedelivery(string grantAnswer)
    {
        // An interrupt cancels the work while its grant is in flight; the command stream
        // then delivers the same handoff again, and the platform answers this instance GRANTED.
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var command = Command();
        var invocations = 0;
        var acknowledgements = new List<AcknowledgePromptResultRequest>();
        var grantSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(work.Start(command, false, (captured, physical) => Execute(captured,
            async (_, token) =>
            {
                grantSent.TrySetResult();
                await interrupted.Task;
                if (grantAnswer == "unsettled") await Task.Delay(Timeout.Infinite, token);
                return await Granted();
            },
            (_, _) => { invocations++; return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, "{}"u8.ToArray(), "ran")); },
            acknowledge: (ack, _) => { acknowledgements.Add(ack.Clone()); return Task.CompletedTask; },
            cancellationToken: physical,
            onGranted: () => work.MarkGranted(captured.HandoffId),
            grantSettleTimeout: TimeSpan.FromMilliseconds(100))), Is.True);
        await grantSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        work.Cancel(_ => true);
        interrupted.SetResult();
        await work.JoinCurrentOperationsAsync().WaitAsync(TimeSpan.FromSeconds(5));

        var redelivered = work.Start(command, false, (captured, physical) => Execute(captured, (_, _) => Granted(),
            (_, _) => { invocations++; return Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, "{}"u8.ToArray(), "ran")); },
            cancellationToken: physical));
        await work.CancelAndWaitAsync(_ => true);
        Assert.Multiple(() =>
        {
            Assert.That(redelivered, Is.False);
            Assert.That(invocations, Is.Zero);
            Assert.That(acknowledgements.Select(ack => ack.Outcome),
                Is.EqualTo(grantAnswer == "answered" ? new[] { PromptResultOutcome.NotExecuted } : Array.Empty<PromptResultOutcome>()));
        });
    }

    [Test]
    public async Task AMissingArtifactUploaderAfterTheGrantIsAcknowledgedNotExecuted()
    {
        var command = Command();
        AcknowledgePromptResultRequest? acknowledged = null;
        await Execute(command, (_, _) => Granted(),
            (_, _) => throw new AssertionException("Missing uploader invoked the runtime"),
            acknowledge: (ack, _) => { acknowledged = ack.Clone(); return Task.CompletedTask; },
            captureRefresh: _ => throw new InvalidOperationException("The captured artifact uploader is not active."));
        Assert.Multiple(() =>
        {
            Assert.That(acknowledged!.HandoffId, Is.EqualTo(command.HandoffId));
            Assert.That(acknowledged.Outcome, Is.EqualTo(PromptResultOutcome.NotExecuted));
            Assert.That(acknowledged.DiagnosticCode, Is.EqualTo("prompt_preparation_incomplete"));
        });
    }

    [Test]
    public async Task FailedLocalForgePreparationBeforeNativeIoIsAcknowledgedNotExecuted()
    {
        var command = Command();
        command.EditorRuntimeKind = "forge-neo";
        command.Request.Path = "queue/join";
        AcknowledgePromptResultRequest? acknowledged = null;
        await Execute(command, (_, _) => Granted(),
            (_, _) => throw new System.Text.Json.JsonException("local Forge request preparation failed"),
            acknowledge: (ack, _) => { acknowledged = ack.Clone(); return Task.CompletedTask; },
            nativeRequestAttempted: false);
        Assert.That(acknowledged!.Outcome, Is.EqualTo(PromptResultOutcome.NotExecuted));
        Assert.That(acknowledged.DiagnosticCode, Is.EqualTo("prompt_preparation_incomplete"));
    }

    [Test]
    public async Task ANativeErrorStatusIsAcknowledgedAsTheNativeResponse()
    {
        var command = Command();
        AcknowledgePromptResultRequest? observed = null;
        await Execute(command, (_, _) => Granted(),
            (_, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(false, 503, [1, 2, 3], "not generation evidence", "application/octet-stream")),
            acknowledge: (ack, _) => { observed = ack; return Task.CompletedTask; });
        Assert.That(observed!.Outcome, Is.EqualTo(PromptResultOutcome.NativeResponse));
        Assert.That(observed.StatusCode, Is.EqualTo(503));
        Assert.That(observed.Body.ToByteArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
        Assert.That(observed.ContentType, Is.EqualTo("application/octet-stream"));
    }

    [Test]
    public async Task AnOmittedNativeBodyKeepsTheExactStatusAndSaysSo()
    {
        var command = Command();
        AcknowledgePromptResultRequest? observed = null;
        await Execute(command, (_, _) => Granted(),
            (_, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200, [], "Prompt submitted", "application/json", BodyOmitted: true)),
            acknowledge: (ack, _) => { observed = ack; return Task.CompletedTask; });
        Assert.Multiple(() =>
        {
            Assert.That(observed!.Outcome, Is.EqualTo(PromptResultOutcome.NativeResponse));
            Assert.That(observed.StatusCode, Is.EqualTo(200));
            Assert.That(observed.Body.IsEmpty, Is.True);
            Assert.That(observed.BodyOmitted, Is.True);
            Assert.That(observed.DiagnosticCode, Is.EqualTo(PromptProtocolCodes.NativeResponseBodyOmitted));
        });
    }

    [Test]
    public async Task TheBridgeUsesTheClientNamedByTheCommandAndMapsTheReturnedPromptId()
    {
        var command = Command();
        command.ClientId = "client-7";
        command.Request.Body = "{\"client_id\":\"body-client\"}";
        string? bridgedClient = null;
        (string Client, string PromptId, string HandoffId)? mapped = null;
        await Execute(command, (_, _) => Granted(),
            (_, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(true, 200,
                "{\"prompt_id\":\"8d6f9c8e-6f1f-4a4b-9c55-6b2f1d3c4e5f\",\"number\":3}"u8.ToArray(), "Prompt submitted")),
            ensureBridge: (_, _, clientId, _) => { bridgedClient = clientId; return Task.CompletedTask; },
            mapPrompt: (_, clientId, promptId, handoffId) => mapped = (clientId, promptId, handoffId));
        Assert.Multiple(() =>
        {
            Assert.That(bridgedClient, Is.EqualTo("client-7"));
            Assert.That(mapped, Is.EqualTo(("client-7", "8d6f9c8e-6f1f-4a4b-9c55-6b2f1d3c4e5f", command.HandoffId)));
        });
    }

    [Test]
    public async Task ARejectedPromptMapsNoPromptId()
    {
        var command = Command();
        command.ClientId = "client-7";
        var mapped = false;
        await Execute(command, (_, _) => Granted(),
            (_, _) => Task.FromResult(new SessionProcessManager.PromptSubmissionResult(false, 400,
                "{\"prompt_id\":\"8d6f9c8e-6f1f-4a4b-9c55-6b2f1d3c4e5f\"}"u8.ToArray(), "rejected")),
            mapPrompt: (_, _, _, _) => mapped = true);
        Assert.That(mapped, Is.False);
    }

    [Test]
    public void RetiredInterruptDoesNotCancelPreparationForAReplacementRuntime()
    {
        var command = Command();
        var interrupt = new RelayEditorRuntimeRequestCommand
        {
            SessionId = command.SessionId,
            LifecycleGeneration = command.LifecycleGeneration,
            RuntimeEpoch = command.RuntimeEpoch,
            RuntimeInstanceId = command.RuntimeInstanceId,
        };
        Assert.That(ServiceClientManager.TargetsPromptRuntime(interrupt, PromptWorkIdentity.Capture(command, false)), Is.True);
        command.RuntimeEpoch++;
        command.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        Assert.That(ServiceClientManager.TargetsPromptRuntime(interrupt, PromptWorkIdentity.Capture(command, false)), Is.False);
    }

    [Test]
    public async Task APromptNeedingADownloadWhileDownloadsAreOffIsAcknowledgedNotExecutedWithTheReason()
    {
        var command = Command();
        command.Downloads.Add(new ModelDownloadRequest
        {
            ModelId = "model-1",
            Filename = "flux-dev.safetensors",
            DestinationPath = "/comfyui/models/diffusion_models/flux-dev.safetensors",
            SourceUrl = "https://models.example.test/flux-dev.safetensors",
        });
        AcknowledgePromptResultRequest? acknowledged = null;
        await Execute(command, (_, _) => Granted(),
            (_, _) => throw new AssertionException("A refused prompt invoked the runtime"),
            acknowledge: (ack, _) => { acknowledged = ack.Clone(); return Task.CompletedTask; },
            prepareDownloads: (downloads, _, _) => throw new ModelDownloadsDisabledException(downloads.Select(download => download.Filename).ToArray()));

        Assert.Multiple(() =>
        {
            Assert.That(acknowledged!.Outcome, Is.EqualTo(PromptResultOutcome.NotExecuted));
            Assert.That(acknowledged.DiagnosticCode, Is.EqualTo("model_downloads_disabled"));
        });
    }

    private static SubmitPromptCommand Command() => new()
    {
        HandoffId = Guid.NewGuid().ToString("n"),
        SessionId = "22222222222242228222222222222222",
        EditorSid = "editor-1",
        Owner = "alice",
        EditorRuntimeKind = "comfyui",
        LifecycleGeneration = 1,
        RuntimeEpoch = 1,
        RuntimeInstanceId = "44444444444444448444444444444444",
        Request = new() { Method = "POST", Path = "api/prompt", Body = "{}" },
    };

    private static Task<GrantPromptExecutionResponse> Granted()
        => Task.FromResult(new GrantPromptExecutionResponse { Outcome = PromptGrantOutcome.Granted });

    private static Task Execute(SubmitPromptCommand command,
        Func<GrantPromptExecutionRequest, CancellationToken, Task<GrantPromptExecutionResponse>> grant,
        Func<SubmitPromptCommand, CancellationToken, Task<SessionProcessManager.PromptSubmissionResult>> invoke,
        Func<AcknowledgePromptResultRequest, CancellationToken, Task>? acknowledge = null,
        CancellationToken cancellationToken = default,
        Func<string, Func<Task>>? captureRefresh = null,
        bool nativeRequestAttempted = true,
        Func<IEnumerable<ModelDownloadRequest>, CancellationToken, bool, Task<Task>>? prepareDownloads = null,
        Func<SubmitPromptCommand, bool>? isRuntimeCurrent = null,
        Func<string, string, string, CancellationToken, Task>? ensureBridge = null,
        Action<string, string, string, string>? mapPrompt = null,
        Action? onGranted = null,
        CancellationToken hostShutdown = default,
        TimeSpan? grantSettleTimeout = null)
        => ServiceClientManager.HandleSubmitEditorActionAsync(
            acknowledge ?? ((_, _) => Task.CompletedTask), (_, _) => Task.CompletedTask,
            command, RunnerId, InstanceId, isRuntimeCurrent ?? (_ => true), grant,
            ensureBridge ?? ((_, _, _, _) => Task.CompletedTask), _ => { },
            (_, _, _, _) => Task.CompletedTask, (_, _, _, _, _) => Task.CompletedTask,
            captureRefresh ?? (_ => () => Task.CompletedTask), (_, _) => { }, prepareDownloads ?? ((_, _, _) => Task.FromResult(Task.CompletedTask)),
            (captured, _, onNativeRequestAttempt, token) =>
            {
                if (nativeRequestAttempted) onNativeRequestAttempt();
                return invoke(captured, token);
            }, mapPrompt ?? ((_, _, _, _) => { }), cancellationToken, onGranted: onGranted,
            hostShutdown: hostShutdown, grantSettleTimeout: grantSettleTimeout);
}

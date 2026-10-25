using Offloadr.Runner.V1;
using Offloadr.Runner.Core;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.ExceptionServices;

namespace Offloadr.Runner.Tests;

public sealed partial class ComfyPromptEvidenceTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task NativeTerminalFrame_WithdrawsOnlyMatchingRuntimeAndJoinsDemandCallbacks(bool callbackFails)
    {
        using var portReservation = new TcpListener(IPAddress.Loopback, 0);
        portReservation.Start();
        var port = ((IPEndPoint)portReservation.LocalEndpoint).Port;
        portReservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var command = Command();
        var replacement = command.Clone();
        replacement.CommandId = Guid.NewGuid().ToString("n");
        replacement.Target.RuntimeEpoch++;
        replacement.Target.RuntimeInstanceId = Guid.NewGuid().ToString("n");
        var work = new PromptCommandWorkSet(CancellationToken.None);
        var originalDemand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherDemand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        CancellationToken otherToken = default;
        Task? stop = null;
        Task? secondStop = null;
        Exception? bodyFailure = null;
        Exception? cleanupFailure = null;
        var callbacks = 0;
        await using var relay = new ComfySessionEventRelay(static (_, _) => { }, command.Target.RunnerId,
            "127.0.0.1", port, "test-process", _ => new SessionSealingStatus(), isTrackedRuntime: (_, _) => true);
        relay.AttachSink(new EvidenceSink((_, _) => Task.CompletedTask));
        void Withdraw(ComfySessionEventRelay.PromptTerminalEvent finished)
        {
            work.WithdrawDemand(prompt => ServiceClientManager.IsFinishedPrompt(prompt, finished));
            terminal.TrySetResult();
        }
        relay.PromptFinished += Withdraw;
        try
        {
            Assert.That(work.Start(command, false, (_, _, demand) =>
            {
                var lifetime = new OwnedCancellation(demand);
                registration = lifetime.Token.Register(() =>
                {
                    Interlocked.Increment(ref callbacks);
                    callbackEntered.TrySetResult();
                    releaseCallback.Task.GetAwaiter().GetResult();
                    if (callbackFails) throw new InvalidOperationException("synthetic terminal callback failure");
                });
                work.HoldDemand(command.CommandId, ServiceClientManager.SettlePromptDemandAsync(originalDemand.Task, true, lifetime));
                return Task.CompletedTask;
            }), Is.True);
            Assert.That(work.Start(replacement, false, (_, _, demand) =>
            {
                var lifetime = new OwnedCancellation(demand);
                otherToken = lifetime.Token;
                work.HoldDemand(replacement.CommandId, ServiceClientManager.SettlePromptDemandAsync(otherDemand.Task, true, lifetime));
                return Task.CompletedTask;
            }), Is.True);
            await relay.EnsureBridgeAsync(command.SessionId, command.EditorSid, "client", command.Target.GpuGeneration,
                command.Target.RuntimeEpoch, command.Target.RuntimeInstanceId, [], CancellationToken.None);
            var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5));
            using var socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            relay.RegisterSubmission(command.SessionId, "client", command);
            await socket.SendAsync(Frame(command).Payload.Memory, WebSocketMessageType.Text, true, CancellationToken.None);
            await terminal.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            originalDemand.SetResult();
            stop = work.CancelAndWaitAsync(prompt => prompt.CommandId == command.CommandId);
            secondStop = work.CancelAndWaitAsync(prompt => prompt.CommandId == command.CommandId);
            await Task.Delay(50);
            Assert.Multiple(() =>
            {
                Assert.That(stop.IsCompleted, Is.False, "The terminal event cannot release an original callback still in progress.");
                Assert.That(secondStop.IsCompleted, Is.False);
                Assert.That(otherToken.IsCancellationRequested, Is.False, "A terminal frame from the older runtime cannot withdraw the replacement's demand.");
                Assert.That(callbacks, Is.EqualTo(1));
            });
            releaseCallback.SetResult();
            await Task.WhenAll(stop, secondStop).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(otherToken.IsCancellationRequested, Is.False);
        }
        catch (Exception error) { bodyFailure = error; }
        finally
        {
            relay.PromptFinished -= Withdraw;
            originalDemand.TrySetResult();
            otherDemand.TrySetResult();
            releaseCallback.TrySetResult();
            try
            {
                await Task.WhenAll(stop ?? Task.CompletedTask, secondStop ?? Task.CompletedTask,
                    work.CancelAndWaitAsync(_ => true)).WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception error) { cleanupFailure = error; }
            registration.Dispose();
        }
        if (bodyFailure is not null) ExceptionDispatchInfo.Capture(bodyFailure).Throw();
        if (cleanupFailure is not null) ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
    }
}

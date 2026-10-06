using System.Net;
using System.Reflection;

namespace Offloadr.Runner.Tests;

public class Aria2ProcessManagerTests
{
    [Test]
    public void EnsureRawRpcResponse_HttpFailurePreservesStructuredAria2Failure()
    {
        const string responseBody =
            """{"id":"request-id","jsonrpc":"2.0","error":{"code":1,"message":"GID 0123456789abcdef is not found"}}""";
        using var response = new HttpResponseMessage(HttpStatusCode.BadRequest);

        var exception = Assert.Throws<Aria2RawRpcException>(() =>
            Aria2ProcessManager.EnsureRawRpcResponse(
                "aria2.changePosition",
                response,
                responseBody));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.ResultCode, Is.EqualTo(1));
            Assert.That(exception.ResultMessage, Is.EqualTo("GID 0123456789abcdef is not found"));
            Assert.That(exception.Message, Does.Contain("aria2.changePosition"));
        });
    }

    [Test]
    public async Task BuildArguments_UsesSingleQueueAndSuppressesRawConsoleOutput()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
        await using var manager = new Aria2ProcessManager(settings);
        var buildArguments = typeof(Aria2ProcessManager).GetMethod(
            "BuildArguments",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var arguments = ((IEnumerable<string>)buildArguments!.Invoke(manager, null)!).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(
                arguments.Where(static argument => argument.StartsWith(
                    "--max-concurrent-downloads=",
                    StringComparison.Ordinal)),
                Is.EqualTo(["--max-concurrent-downloads=1"]));
            Assert.That(arguments, Does.Contain("--quiet=true"));
            Assert.That(
                arguments,
                Has.None.StartsWith("--console-log-level="));
        });
    }

    [Test]
    public async Task BuildArguments_UsesRunnerProcessWatchdogByDefault()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
        await using var manager = new Aria2ProcessManager(settings);
        var buildArguments = typeof(Aria2ProcessManager).GetMethod(
            "BuildArguments",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var arguments = ((IEnumerable<string>)buildArguments!.Invoke(manager, null)!).ToArray();

        Assert.That(
            arguments.Where(static argument => argument.StartsWith(
                "--stop-with-process=",
                StringComparison.Ordinal)),
            Is.EqualTo([$"--stop-with-process={Environment.ProcessId}"]));
    }

    [Test]
    public async Task BuildArguments_CanDisableProcessWatchdogForIntentionalRestart()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
        await using var manager = new Aria2ProcessManager(settings, useProcessWatchdog: false);
        var buildArguments = typeof(Aria2ProcessManager).GetMethod(
            "BuildArguments",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var arguments = ((IEnumerable<string>)buildArguments!.Invoke(manager, null)!).ToArray();

        Assert.That(
            arguments,
            Has.None.StartsWith("--stop-with-process="));
    }
}

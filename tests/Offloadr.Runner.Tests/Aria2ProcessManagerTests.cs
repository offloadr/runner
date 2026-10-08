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
    public void RedactForLog_RemovesSignedUriPathsAndQueries_AndMasksSecret()
    {
        const string addUri =
            """{"jsonrpc":"2.0","method":"aria2.addUri","id":"1","params":["token:rpc-secret-value",["https://cdn.example.com/models/a.safetensors?X-Amz-Signature=abc123&X-Amz-Credential=key"],{"out":"a.safetensors"}]}""";
        const string tellStatus =
            """{"id":"2","result":{"files":[{"uris":[{"status":"used","uri":"https:\/\/user:pw@cdn.example.com:8443\/sig\/token-in-path?Expires=1&Signature=xyz"}]}]}}""";
        const string consoleLine = "Exception: [AbstractCommand.cc:351] URI=http://mirror.example.org/file.bin?token=t0k3n";

        var redactedAddUri = Aria2ProcessManager.RedactForLog(addUri, "rpc-secret-value");
        var redactedStatus = Aria2ProcessManager.RedactForLog(tellStatus, "rpc-secret-value");
        var redactedLine = Aria2ProcessManager.RedactForLog(consoleLine, "rpc-secret-value");

        Assert.Multiple(() =>
        {
            Assert.That(redactedAddUri, Does.Contain("https://cdn.example.com/<redacted>\""));
            Assert.That(redactedAddUri, Does.Contain("token:***"));
            Assert.That(redactedAddUri, Does.Contain("\"out\":\"a.safetensors\""));
            Assert.That(redactedAddUri, Does.Not.Contain("rpc-secret-value"));
            Assert.That(redactedAddUri, Does.Not.Contain("Signature"));
            Assert.That(redactedAddUri, Does.Not.Contain("abc123"));
            Assert.That(redactedAddUri, Does.Not.Contain("models/a.safetensors"));

            Assert.That(redactedStatus, Does.Contain("https:\\/\\/cdn.example.com:8443/<redacted>\""));
            Assert.That(redactedStatus, Does.Not.Contain("pw@"));
            Assert.That(redactedStatus, Does.Not.Contain("token-in-path"));
            Assert.That(redactedStatus, Does.Not.Contain("Signature"));
            Assert.That(redactedStatus, Does.Not.Contain("xyz"));

            Assert.That(redactedLine, Does.EndWith("URI=http://mirror.example.org/<redacted>"));
            Assert.That(redactedLine, Does.Not.Contain("t0k3n"));
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

    [Test]
    public async Task BuildArguments_KeepsRpcOnLoopbackAndSecretOutOfArgv()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "rpc-secret-value");
        await using var manager = new Aria2ProcessManager(settings);
        var buildArguments = typeof(Aria2ProcessManager).GetMethod(
            "BuildArguments",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var arguments = ((IEnumerable<string>)buildArguments!.Invoke(manager, null)!).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(arguments, Has.None.Contains("rpc-secret-value"));
            Assert.That(arguments, Has.None.StartsWith("--rpc-secret"));
            Assert.That(arguments, Does.Contain("--rpc-listen-all=false"));
            Assert.That(arguments, Has.None.StartsWith("--rpc-allow-origin-all"));
            Assert.That(arguments, Does.Contain($"--conf-path={manager.RpcConfigPath}"));
        });
    }

    [Test]
    public async Task BuildStartInfo_RunsAsServiceAccountWithoutAgentEnvironment()
    {
        var settings = Aria2Settings.FromEnvironment(
            name => name == "ARIA2_STATE_DIR" ? "/var/lib/aria2-test" : null,
            static () => "secret");
        var account = new Aria2ServiceAccount("offloadr-aria2", 62000, 62000);
        await using var manager = new Aria2ProcessManager(settings, fileAccess: new FixedFileAccess(account));
        const string agentOnlyVariable = "RUNNER_SECRET";
        var previous = Environment.GetEnvironmentVariable(agentOnlyVariable);
        Environment.SetEnvironmentVariable(agentOnlyVariable, "runner-secret-value");
        try
        {
            var startInfo = manager.BuildStartInfo();

            Assert.Multiple(() =>
            {
                Assert.That(startInfo.UserName, Is.EqualTo("offloadr-aria2"));
                Assert.That(startInfo.WorkingDirectory, Is.EqualTo("/var/lib/aria2-test"));
                Assert.That(startInfo.Environment.ContainsKey(agentOnlyVariable), Is.False);
                Assert.That(startInfo.Environment["HOME"], Is.EqualTo("/var/lib/aria2-test"));
                Assert.That(startInfo.Environment["USER"], Is.EqualTo("offloadr-aria2"));
                Assert.That(startInfo.ArgumentList, Does.Contain($"--stop-with-process={Environment.ProcessId}"));
                Assert.That(startInfo.ArgumentList, Does.Contain($"--conf-path={manager.RpcConfigPath}"));
                Assert.That(startInfo.ArgumentList, Does.Contain("--rpc-listen-all=false"));
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable(agentOnlyVariable, previous);
        }
    }

    [Test]
    public async Task BuildStartInfo_WithoutServiceAccount_RunsAsAgentUser()
    {
        var settings = Aria2Settings.FromEnvironment(static _ => null, static () => "secret");
        await using var manager = new Aria2ProcessManager(settings, fileAccess: new FixedFileAccess(null));

        var startInfo = manager.BuildStartInfo();

        Assert.That(startInfo.UserName, Is.Empty);
    }

    [Test]
    public void ReadSessionTargets_ReturnsEachEntrysDirectoryAndOutput()
    {
        string[] lines =
        [
            "https://cdn.example.com/a.safetensors\thttps://mirror.example.com/a.safetensors",
            " dir=/comfyui/models/checkpoints",
            " gid=0123456789abcdef",
            " out=a.safetensors",
            "https://cdn.example.com/b.bin",
            " out=b.bin",
            "https://cdn.example.com/c.bin",
            " dir=/models",
            " out=c.bin"
        ];

        var targets = Aria2ProcessManager.ReadSessionTargets(lines).ToArray();

        Assert.That(
            targets,
            Is.EqualTo(new[]
            {
                Path.Combine("/comfyui/models/checkpoints", "a.safetensors"),
                Path.Combine("/models", "c.bin")
            }));
    }

    private sealed class FixedFileAccess(Aria2ServiceAccount? account) : IAria2FileAccess
    {
        public Aria2ServiceAccount? Account => account;

        public void PrepareStateDirectory(Aria2Settings settings, string rpcConfigPath)
        {
        }

        public void PrepareTransferTarget(string destinationPath)
        {
        }
    }

    [Test]
    public async Task WriteRpcConfig_WritesSecretToOwnerOnlyFile()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"aria2-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var settings = Aria2Settings.FromEnvironment(
                name => name == "ARIA2_STATE_DIR" ? stateDirectory : null,
                static () => "rpc-secret-value");
            await using var manager = new Aria2ProcessManager(settings);
            File.WriteAllText(manager.RpcConfigPath, "stale");

            manager.WriteRpcConfig();

            Assert.That(File.ReadAllText(manager.RpcConfigPath), Is.EqualTo("rpc-secret=rpc-secret-value\n"));
            if (!OperatingSystem.IsWindows())
            {
                Assert.That(
                    File.GetUnixFileMode(manager.RpcConfigPath),
                    Is.EqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite));
            }
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Test]
    public async Task WriteRpcConfig_RejectsMultiLineSecret()
    {
        var stateDirectory = Path.Combine(Path.GetTempPath(), $"aria2-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var settings = Aria2Settings.FromEnvironment(
                name => name switch
                {
                    "ARIA2_STATE_DIR" => stateDirectory,
                    "ARIA2_RPC_SECRET" => "first\nrpc-listen-all=true",
                    _ => null,
                },
                static () => "unused");
            await using var manager = new Aria2ProcessManager(settings);

            Assert.Throws<InvalidOperationException>(manager.WriteRpcConfig);
            Assert.That(File.Exists(manager.RpcConfigPath), Is.False);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }
}

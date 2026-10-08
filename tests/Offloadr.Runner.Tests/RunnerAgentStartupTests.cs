using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public class RunnerAgentStartupTests
{
    [Test]
    public async Task FailedStartupValidation_ExitsNonZero()
    {
        var agentPath = Path.Combine(AppContext.BaseDirectory, "RunnerAgent.dll");
        Assert.That(File.Exists(agentPath), Is.True, agentPath);

        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var startInfo = new ProcessStartInfo
        {
            FileName = string.IsNullOrWhiteSpace(dotnet) ? "dotnet" : dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(agentPath);
        startInfo.Environment["RUNNER_SECRET"] = string.Empty;
        startInfo.Environment["RUNNER_ID"] = "not-a-uuid";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The agent did not exit after failing startup validation.");
        }

        var output = await stdout + await stderr;
        Assert.That(output, Does.Contain("Runner startup validation failed"));
        Assert.That(process.ExitCode, Is.Not.EqualTo(0));
    }
}

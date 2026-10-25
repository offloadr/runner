using System.Diagnostics;

namespace Offloadr.Runner.Tests;

public class BoundedLineReaderLinuxTests
{
    [Test]
    public async Task PumpAsync_CancelsAnOriginalProcessPipeReadWhileTheWriterStaysAlive()
    {
        LinuxTestPrerequisites.RequireLinux();
        LinuxTestPrerequisites.RequireCommand("/bin/sh");
        using var stopping = new CancellationTokenSource();
        var start = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardInput = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf 'ready\\n'; read -r release; printf 'late\\n'");
        using var process = Process.Start(start)!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new ObservedPipeReader(process.StandardOutput, ready.Task);
        var lines = new List<string>();
        var pump = BoundedLineReader.PumpAsync(reader, line =>
        {
            lines.Add(line);
            if (line == "ready") ready.TrySetResult();
        }, cancellationToken: stopping.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await reader.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await stopping.CancelAsync();
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(reader.Returned.Task.IsCompletedSuccessfully, Is.True);
            Assert.That(process.HasExited, Is.False, "the writer must still own the pipe when its original read is joined");

            await process.StandardInput.WriteLineAsync("release");
            await process.StandardInput.FlushAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(lines, Is.EqualTo(new[] { "ready" }));
        }
        finally
        {
            await stopping.CancelAsync();
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await pump.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class ObservedPipeReader(StreamReader source, Task ready) : TextReader
    {
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            var originalRead = source.ReadAsync(buffer, cancellationToken);
            var observe = ready.IsCompletedSuccessfully && !originalRead.IsCompleted;
            if (observe) Blocked.TrySetResult();
            try { return await originalRead; }
            finally { if (observe) Returned.TrySetResult(); }
        }
    }
}

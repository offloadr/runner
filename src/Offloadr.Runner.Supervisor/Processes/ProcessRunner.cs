using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Offloadr.Runner.Supervisor.Processes;

/// <summary>
/// A command line. Environment entries are added to the child's environment and never appear in its
/// arguments; <paramref name="RemovedEnvironment"/> names variables the child does not inherit.
/// </summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string>? Environment = null,
    IReadOnlyList<string>? RemovedEnvironment = null)
{
    /// <summary>
    /// Asked at the last moment before the process is started: false, and it is not (a
    /// <see cref="ProcessNotSpawnedException"/>). For what can change without a token being cancelled,
    /// a permission that runs out with time, say.
    /// </summary>
    public Func<bool>? StillWanted { get; init; }
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;
}

/// <summary>Thrown when the executable cannot be started at all.</summary>
public sealed class ProcessStartException(string fileName, Exception inner)
    : Exception($"Could not start '{fileName}': {inner.Message}", inner);

/// <summary>
/// The command was cancelled before its process was started: nothing of it reached what it was to be
/// asked of. Unlike a command cancelled while it ran, which may have been heard.
/// </summary>
public sealed class ProcessNotSpawnedException(CancellationToken cancellationToken)
    : OperationCanceledException("The command was cancelled before its process was started.", cancellationToken);

public interface IProcessRunner
{
    /// <summary>Runs to completion and captures output. Cancellation kills the process tree.</summary>
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken);

    /// <summary>Runs to completion, reporting each stdout and stderr line as it arrives. Returns the exit code.</summary>
    Task<int> StreamAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken);
}

public sealed class SystemProcessRunner : IProcessRunner
{
    /// <summary>How long a cancelled command may take to be gone after it is killed.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(10);

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var exitCode = await RunCoreAsync(
            spec,
            line => { lock (stdout) { stdout.AppendLine(line); } },
            line => { lock (stderr) { stderr.AppendLine(line); } },
            cancellationToken).ConfigureAwait(false);
        return new ProcessResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    public Task<int> StreamAsync(ProcessSpec spec, Action<string> onLine, CancellationToken cancellationToken)
        => RunCoreAsync(spec, onLine, onLine, cancellationToken);

    private static async Task<int> RunCoreAsync(
        ProcessSpec spec,
        Action<string> onStdout,
        Action<string> onStderr,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            throw new ProcessNotSpawnedException(cancellationToken);
        }

        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in spec.RemovedEnvironment ?? [])
        {
            startInfo.Environment.Remove(name);
        }

        if (spec.Environment is not null)
        {
            foreach (var (name, value) in spec.Environment)
            {
                startInfo.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => { if (e.Data is null) { stdoutDone.TrySetResult(); } else { onStdout(e.Data); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) { stderrDone.TrySetResult(); } else { onStderr(e.Data); } };

        // Looked at once more, at the last moment before the process exists: cancelled while it was being
        // prepared, nothing of the command is started, and the caller is told so (a start of a container
        // that was held back is not one whose answer was lost).
        // (The token last: it may be cancelled while the other is asked.)
        if (spec.StillWanted?.Invoke() == false || cancellationToken.IsCancellationRequested)
        {
            throw new ProcessNotSpawnedException(cancellationToken);
        }

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            throw new ProcessStartException(spec.FileName, ex);
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            // Killing only asks. The caller cleans up what the command may have created (a cancelled
            // `docker run`, say), so it gets control back only once the command is gone.
            using var exited = new CancellationTokenSource(KillWait);
            try
            {
                await process.WaitForExitAsync(exited.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // It did not go in time; the cleanup still runs, as before.
            }

            throw;
        }

        await Task.WhenAll(stdoutDone.Task, stderrDone.Task).ConfigureAwait(false);
        return process.ExitCode;
    }
}

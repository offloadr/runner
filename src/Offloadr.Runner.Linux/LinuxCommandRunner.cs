using System.Diagnostics;

namespace Offloadr.Runner.Linux;

public readonly record struct LinuxCommand(string FileName, IReadOnlyList<string> Arguments, bool ThrowOnError = true);

public readonly record struct LinuxCommandResult(int ExitCode, string Stdout, string Stderr);

public interface ILinuxCommandRunner
{
    Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken);
}

public static class LinuxCommandFactory
{
    public static LinuxCommand CheckUserExists(string userName)
        => new("/usr/bin/id", ["-u", userName], ThrowOnError: false);

    public static LinuxCommand CreateUser(string userName, string homeDirectory)
        => new(
            "/usr/sbin/useradd",
            ["--home-dir", homeDirectory, "-m", "-U", "--shell", "/usr/sbin/nologin", userName],
            ThrowOnError: true);

    public static LinuxCommand ChownRecursive(string userName, string path)
    {
        // Session virtualenvs contain symlinks to the immutable seed venv; never retag symlink targets.
        return new("/bin/chown", ["-R", "-h", $"{userName}:{userName}", path], ThrowOnError: true);
    }

    public static LinuxCommand SendSignal(int pid, string signal)
        => new("/bin/kill", [$"-{signal}", pid.ToString()], ThrowOnError: false);

    public static LinuxCommand RemoveUser(string userName)
        => new("/usr/sbin/userdel", ["--force", "--remove", userName], ThrowOnError: false);
}

public sealed class LinuxCommandRunner : ILinuxCommandRunner
{
    public async Task<LinuxCommandResult> RunAsync(LinuxCommand command, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            }
        };

        foreach (var arg in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // Ignore process kill failures during cancellation.
            }

            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var result = new LinuxCommandResult(process.ExitCode, stdout, stderr);

        if (result.ExitCode != 0 && command.ThrowOnError)
        {
            var args = string.Join(' ', command.Arguments);
            throw new InvalidOperationException($"Command '{command.FileName} {args}' failed with exit code {result.ExitCode}: {result.Stderr.Trim()} {result.Stdout.Trim()}");
        }

        return result;
    }
}

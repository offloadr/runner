namespace Offloadr.Runner.Linux;

public readonly record struct PreparedSessionIdentity(string UserName, string HomeDirectory, bool CleanupIdentity);

public interface ISessionIsolationStrategy
{
    void ValidatePrerequisites(ICollection<string> errors);
    Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken cancellationToken);
    Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken);
}

public sealed class LinuxUserIsolationStrategy : ISessionIsolationStrategy
{
    private readonly ILinuxCommandRunner _commandRunner;

    public LinuxUserIsolationStrategy(ILinuxCommandRunner commandRunner)
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
    }

    public void ValidatePrerequisites(ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        if (!string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("Linux user isolation requires running as root so useradd/chown/userdel can execute.");
        }

        if (!File.Exists("/usr/bin/id"))
        {
            errors.Add("Missing '/usr/bin/id' required for Linux user isolation.");
        }

        if (!File.Exists("/usr/sbin/useradd"))
        {
            errors.Add("Missing '/usr/sbin/useradd' required for Linux user isolation.");
        }

        if (!File.Exists("/bin/chown"))
        {
            errors.Add("Missing '/bin/chown' required for Linux user isolation.");
        }

        if (!File.Exists("/usr/sbin/userdel"))
        {
            errors.Add("Missing '/usr/sbin/userdel' required for Linux user isolation.");
        }
    }

    public async Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("Session id is required.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(sessionRoot))
        {
            throw new ArgumentException("Session root is required.", nameof(sessionRoot));
        }

        // Validate before any filesystem or user operation: the home directory is
        // later chowned recursively and removed with the user.
        var normalizedSessionId = sessionId.Trim();
        var homeDirectory = SessionHomePaths.ResolveHomeDirectory(sessionRoot, normalizedSessionId);
        var userName = LinuxSessionIdentity.BuildUserName(normalizedSessionId);

        bool userCreated = false;
        try
        {
            Directory.CreateDirectory(sessionRoot);
            var exists = await _commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(userName), cancellationToken).ConfigureAwait(false);
            if (exists.ExitCode != 0)
            {
                await _commandRunner.RunAsync(LinuxCommandFactory.CreateUser(userName, homeDirectory), cancellationToken).ConfigureAwait(false);
                userCreated = true;
            }

            Directory.CreateDirectory(homeDirectory);
            await _commandRunner.RunAsync(LinuxCommandFactory.ChownRecursive(userName, homeDirectory), cancellationToken).ConfigureAwait(false);

            return new PreparedSessionIdentity(userName, homeDirectory, CleanupIdentity: true);
        }
        catch
        {
            if (userCreated)
            {
                try
                {
                    await _commandRunner.RunAsync(LinuxCommandFactory.RemoveUser(userName), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort cleanup for failed setup.
                }
            }

            throw;
        }
    }

    public async Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken)
    {
        if (!identity.CleanupIdentity || string.IsNullOrWhiteSpace(identity.UserName))
        {
            return;
        }

        var result = await _commandRunner.RunAsync(LinuxCommandFactory.RemoveUser(identity.UserName), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.Stderr))
        {
            RunnerLog.Error<LinuxUserIsolationStrategy>($"userdel for '{identity.UserName}' returned {result.ExitCode}: {result.Stderr.Trim()}");
        }
    }
}

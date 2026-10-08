namespace Offloadr.Runner.Linux;

/// <param name="UserId">The numeric uid of <paramref name="UserName"/>, when the strategy created a dedicated user.</param>
public readonly record struct PreparedSessionIdentity(string UserName, string HomeDirectory, bool CleanupIdentity, uint? UserId = null);

public interface ISessionIsolationStrategy
{
    void ValidatePrerequisites(ICollection<string> errors);
    Task<PreparedSessionIdentity> PrepareAsync(string sessionId, string sessionRoot, CancellationToken cancellationToken);
    Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken);

    /// <summary>
    /// Removes session users and homes left behind by an earlier agent run (for example after a
    /// crash). Called at startup, before any session exists.
    /// </summary>
    Task CleanupStaleAsync(string sessionRoot, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class LinuxUserIsolationStrategy : ISessionIsolationStrategy
{
    // userdel's exit status when the user does not exist.
    private const int UserdelUnknownUser = 6;
    private const string SessionUserPrefix = "sess_";

    private readonly ILinuxCommandRunner _commandRunner;
    private readonly Func<uint, CancellationToken, Task<bool>> _killUserProcesses;
    private readonly IReadOnlyList<string> _residueRoots;
    private readonly string _passwdPath;

    public LinuxUserIsolationStrategy(ILinuxCommandRunner commandRunner)
        : this(commandRunner, static (userId, token) => LinuxProcessReaper.KillAllOwnedByAsync(userId, token))
    {
    }

    internal LinuxUserIsolationStrategy(
        ILinuxCommandRunner commandRunner,
        Func<uint, CancellationToken, Task<bool>> killUserProcesses,
        IReadOnlyList<string>? residueRoots = null,
        string passwdPath = "/etc/passwd")
    {
        _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));
        _killUserProcesses = killUserProcesses ?? throw new ArgumentNullException(nameof(killUserProcesses));
        _residueRoots = residueRoots ?? LinuxSessionResidue.SharedTemporaryRoots;
        _passwdPath = passwdPath;
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
        uint? createdUserId = null;
        try
        {
            Directory.CreateDirectory(sessionRoot);
            var exists = await _commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(userName), cancellationToken).ConfigureAwait(false);
            if (exists.ExitCode != 0)
            {
                await _commandRunner.RunAsync(LinuxCommandFactory.CreateUser(userName, homeDirectory), cancellationToken).ConfigureAwait(false);
                userCreated = true;
                exists = await _commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(userName), cancellationToken).ConfigureAwait(false);
            }

            var userId = ParseUserId(userName, exists);
            createdUserId = userCreated ? userId : null;

            // useradd may hand out a uid that an earlier, incompletely cleaned session used, and
            // a pre-existing user may still have processes. Nothing may run as the session uid
            // before the editor starts.
            await KillUserProcessesAsync(userName, userId, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(homeDirectory);
            await _commandRunner.RunAsync(LinuxCommandFactory.ChownRecursive(userName, homeDirectory), cancellationToken).ConfigureAwait(false);

            return new PreparedSessionIdentity(userName, homeDirectory, CleanupIdentity: true, userId);
        }
        catch
        {
            if (userCreated)
            {
                try
                {
                    if (createdUserId is { } userId)
                    {
                        await KillUserProcessesAsync(userName, userId, CancellationToken.None).ConfigureAwait(false);
                    }

                    await _commandRunner.RunAsync(LinuxCommandFactory.RemoveUser(userName), CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort cleanup for failed setup.
                }
            }

            throw;
        }
    }

    private static uint ParseUserId(string userName, LinuxCommandResult result)
    {
        // The IPC socket authenticates session processes by uid, so a session never runs
        // as root or without a known uid.
        if (result.ExitCode != 0 ||
            !uint.TryParse(result.Stdout.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var userId) ||
            userId == 0)
        {
            throw new InvalidOperationException($"Could not resolve a non-root uid for session user '{userName}'.");
        }

        return userId;
    }

    public async Task CleanupAsync(PreparedSessionIdentity identity, CancellationToken cancellationToken)
    {
        if (!identity.CleanupIdentity || string.IsNullOrWhiteSpace(identity.UserName))
        {
            return;
        }

        // Kill everything still running as the session uid, including processes that left the
        // editor's tree, before the user is removed and its uid can be handed out again.
        var userId = identity.UserId;
        if (userId is null)
        {
            var lookup = await _commandRunner.RunAsync(LinuxCommandFactory.CheckUserExists(identity.UserName), cancellationToken).ConfigureAwait(false);
            if (lookup.ExitCode == 0 &&
                uint.TryParse(lookup.Stdout.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var resolved))
            {
                userId = resolved;
            }
        }

        if (userId is { } uid and not 0)
        {
            await KillUserProcessesAsync(identity.UserName, uid, cancellationToken).ConfigureAwait(false);
            RemoveSharedResidue(identity.UserName, uid);
        }

        var result = await _commandRunner.RunAsync(LinuxCommandFactory.RemoveUser(identity.UserName), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0 && result.ExitCode != UserdelUnknownUser && !string.IsNullOrWhiteSpace(result.Stderr))
        {
            RunnerLog.Error<LinuxUserIsolationStrategy>($"userdel for '{identity.UserName}' returned {result.ExitCode}: {result.Stderr.Trim()}");
        }
    }

    public async Task CleanupStaleAsync(string sessionRoot, CancellationToken cancellationToken)
    {
        foreach (var (userName, userId, homeDirectory) in LinuxSessionResidue.ReadUsers(_passwdPath))
        {
            if (!userName.StartsWith(SessionUserPrefix, StringComparison.Ordinal) || userId == 0 ||
                !IsRunnerSessionAccount(sessionRoot, userName, homeDirectory))
            {
                continue;
            }

            RunnerLog.Warning<LinuxUserIsolationStrategy>($"Removing session user '{userName}' left by an earlier run.");
            await CleanupAsync(
                    new PreparedSessionIdentity(userName, string.Empty, CleanupIdentity: true, userId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(sessionRoot))
        {
            return;
        }

        // Homes of removed users are now owned by uids without a passwd entry. The agent's own
        // (root-owned) entries in the session root are left alone.
        var liveUserIds = LinuxSessionResidue.ReadUsers(_passwdPath)
            .Select(static user => user.UserId)
            .ToHashSet();
        var removed = LinuxSessionResidue.RemoveEntriesOfUnknownOwners(sessionRoot, liveUserIds.Contains);
        if (removed > 0)
        {
            RunnerLog.Warning<LinuxUserIsolationStrategy>($"Removed {removed} stale session home(s) from '{sessionRoot}'.");
        }
    }

    /// <summary>
    /// True only for accounts this runner creates: the home is a session folder directly
    /// beneath the session root, named by a session id that maps to this account name.
    /// Other accounts that merely share the prefix are left alone.
    /// </summary>
    internal static bool IsRunnerSessionAccount(string sessionRoot, string userName, string homeDirectory)
    {
        if (!SessionHomePaths.IsDirectChildOf(sessionRoot, homeDirectory))
        {
            return false;
        }

        var sessionId = Path.GetFileName(Path.TrimEndingDirectorySeparator(homeDirectory));
        return SessionHomePaths.IsValidSessionId(sessionId) &&
               string.Equals(LinuxSessionIdentity.BuildUserName(sessionId), userName, StringComparison.Ordinal);
    }

    private void RemoveSharedResidue(string userName, uint userId)
    {
        try
        {
            LinuxSessionResidue.RemoveEntriesOwnedBy(userId, _residueRoots);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<LinuxUserIsolationStrategy>(
                ex,
                $"Failed removing temporary files of session user '{userName}' (uid {userId}): {ex.Message}");
        }
    }

    private async Task KillUserProcessesAsync(string userName, uint userId, CancellationToken cancellationToken)
    {
        if (!await _killUserProcesses(userId, cancellationToken).ConfigureAwait(false))
        {
            RunnerLog.Warning<LinuxUserIsolationStrategy>(
                $"Processes of session user '{userName}' (uid {userId}) were still running after SIGKILL.");
        }
    }
}

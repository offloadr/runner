namespace Offloadr.Runner.Core;

/// <summary>The unprivileged account aria2 runs as.</summary>
internal sealed record Aria2ServiceAccount(string UserName, uint UserId, uint GroupId);

/// <summary>
/// Gives aria2 exactly the filesystem access it needs. aria2 opens files by path, so when it
/// runs as <see cref="Account"/> everything it writes is prepared here first, and a path
/// redirected after validation can only reach what that account may write.
/// </summary>
internal interface IAria2FileAccess
{
    /// <summary>The account aria2 runs as, or null to run it as the agent's own user.</summary>
    Aria2ServiceAccount? Account { get; }

    /// <summary>
    /// Prepares the state directory, session file, RPC configuration and staging directory
    /// before aria2 starts.
    /// </summary>
    void PrepareStateDirectory(Aria2Settings settings, string rpcConfigPath);

    /// <summary>
    /// Lets aria2 write <paramref name="destinationPath"/> and its control file, or throws when
    /// the destination is not in a directory aria2 may write.
    /// </summary>
    void PrepareTransferTarget(string destinationPath);
}

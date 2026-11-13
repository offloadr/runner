using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Gives a Forge Neo session the models it uses as links in its own models directory, each to the
/// model in the runner's cache (<see cref="SessionProcessManager.SessionPaths.ModelCacheDirectory"/>).
/// Forge writes files of its own into that directory, so it stays the session's, while the models
/// outlast the session. Links are made through real directories beneath the session root only, so a
/// session cannot point them, or the directories created for them, anywhere else.
/// </summary>
internal sealed class SessionModelLinks(string sessionRoot)
{
    private readonly string _sessionRoot = Normalize(sessionRoot);

    public void Ensure(SessionProcessManager.SessionPaths paths, IEnumerable<ModelDownloadRequest> downloads)
    {
        if (paths.ModelCacheDirectory is not { Length: > 0 } cacheDirectory)
        {
            return;
        }

        var cache = Normalize(cacheDirectory);
        var links = downloads
            .Where(static download => download is not null &&
                                      !string.IsNullOrWhiteSpace(download.DestinationPath) &&
                                      !string.IsNullOrWhiteSpace(download.RelativeModelPath))
            .Select(download => (Download: download, Target: Normalize(download.DestinationPath)))
            .Where(link => IsBeneath(link.Target, cache))
            .ToArray();
        if (links.Length == 0)
        {
            return;
        }

        using var secureRoot = new LinuxSecureDirectoryRoot(_sessionRoot);
        foreach (var (download, target) in links)
        {
            var linkPath = ServiceClientManager.BuildModelPath(
                paths.ModelsDirectory,
                download.RelativeModelPath,
                download.Filename,
                sessionOwned: false);
            if (!IsBeneath(linkPath, _sessionRoot))
            {
                throw new InvalidOperationException($"Session models directory '{paths.ModelsDirectory}' is outside '{_sessionRoot}'.");
            }

            var relative = Path.GetRelativePath(_sessionRoot, linkPath).Replace(Path.DirectorySeparatorChar, '/');
            switch (secureRoot.ReadSymbolicLink(relative, out var existing))
            {
                case SecureLinkKind.SymbolicLink when string.Equals(existing, target, StringComparison.Ordinal):
                    continue;
                case SecureLinkKind.SymbolicLink:
                    secureRoot.DeleteSymbolicLink(relative);
                    break;
                case SecureLinkKind.Other:
                    // Something the session made itself; it keeps it, and the editor uses it.
                    RunnerLog.Warning<SessionModelLinks>(
                        $"Model '{download.RelativeModelPath}' is not linked into the session: its place in the session's models directory is taken.");
                    continue;
            }

            secureRoot.CreateSymbolicLink(relative, target);
        }
    }

    private static bool IsBeneath(string path, string root)
        => path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}

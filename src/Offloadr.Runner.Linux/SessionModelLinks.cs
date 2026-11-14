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

    // Prompts are prepared concurrently; two that name the same model must not both find its link missing.
    private readonly object _gate = new();

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

        lock (_gate)
        {
            EnsureLinks(paths, links);
        }
    }

    private void EnsureLinks(SessionProcessManager.SessionPaths paths, IReadOnlyList<(ModelDownloadRequest Download, string Target)> links)
    {
        using var secureRoot = new LinuxSecureDirectoryRoot(_sessionRoot);
        InvalidOperationException? conflict = null;
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
                    // Something the session made itself. It keeps it, but the request fails rather than the
                    // editor running on weights other than those asked for; the other models are still linked.
                    conflict ??= new InvalidOperationException(
                        $"Model '{download.RelativeModelPath}' cannot be linked into the session: a file of the session's own is in its place in its models folder.");
                    continue;
            }

            // A category folder made here is the session's, so Forge can still write its own files into it.
            secureRoot.CreateSymbolicLinkInheritingOwner(relative, target);
        }

        if (conflict is not null)
        {
            throw conflict;
        }
    }

    private static bool IsBeneath(string path, string root)
        => path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}

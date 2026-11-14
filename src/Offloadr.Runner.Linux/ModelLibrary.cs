using System.Security.Cryptography;
using System.Text;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Saves the models the runner downloads into the owner's local models folder, beside their own, instead of
/// inside the container: there they are kept, and any other app can use them. The folder is reached twice:
/// <see cref="Directory"/> is a writable mount only the agent and aria2 can reach, and the read-only mount
/// sessions see it through. A download is written to <see cref="StagingFolderName"/> in the folder, so other
/// apps never see half a model, and renamed into place once complete. The editor's path for the model becomes
/// a link to it in the read-only mount.
/// </summary>
internal sealed class ModelLibrary : IModelDownloadStaging, IDownloadLinks
{
    /// <summary>Where downloads in progress are kept, inside the models folder.</summary>
    public const string StagingFolderName = ".offloadr-downloads";

    private readonly string _readOnlyDirectory;
    private readonly string _editorModelsDirectory;
    private readonly string _downloadFolder;
    private readonly string _stagingDirectory;
    private readonly object _gate = new();
    // Editor links this library made, under _gate: the only ones it ever removes.
    private readonly HashSet<string> _ownLinks = new(StringComparer.Ordinal);
    // Editor links a download was sent through (made here, or found in place), under _gate, each with the
    // session that last asked for its model: until that session ends it may be using the model.
    private readonly Dictionary<string, string?> _linkSessions = new(StringComparer.Ordinal);
    // Editor paths where the projector links a local model for the session now, under _gate.
    private readonly HashSet<string> _projectedLinks = new(StringComparer.Ordinal);
    // The links the redirect batch in progress made, under _gate.
    private List<string>? _linksMadeInBatch;

    /// <param name="directory">The models folder, writable.</param>
    /// <param name="readOnlyDirectory">The same folder as sessions see it, read-only.</param>
    /// <param name="editorModelsDirectory">Where the editor finds its models: requests for paths there are sent to the folder.</param>
    /// <param name="runnerId">
    /// This runner's id. Several runners may share one folder, so each keeps its downloads in progress in a
    /// folder of its own and clears only that one.
    /// </param>
    public ModelLibrary(string directory, string readOnlyDirectory, string editorModelsDirectory, string runnerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(readOnlyDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorModelsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
        Directory = Normalize(directory);
        _readOnlyDirectory = Normalize(readOnlyDirectory);
        _editorModelsDirectory = Normalize(editorModelsDirectory);
        _downloadFolder = Path.Combine(Directory, StagingFolderName);
        _stagingDirectory = Path.Combine(_downloadFolder, RunnerFolderName(runnerId));
    }

    private static string RunnerFolderName(string runnerId)
    {
        var trimmed = runnerId.Trim();
        return trimmed.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-')
            ? trimmed
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..32];
    }

    /// <summary>
    /// The download in the folder behind <paramref name="editorPath"/>, when that path is this library's
    /// link: an editor opening the model before it is there waits for that download.
    /// </summary>
    public string? LibraryPathFor(string editorPath)
    {
        var normalized = Normalize(editorPath);
        if (!IsStrictlyBeneath(normalized, _editorModelsDirectory))
        {
            return null;
        }

        var relative = Path.GetRelativePath(_editorModelsDirectory, normalized);
        return string.Equals(new FileInfo(normalized).LinkTarget, Path.Combine(_readOnlyDirectory, relative), StringComparison.Ordinal)
            ? Path.Combine(Directory, relative)
            : null;
    }

    public string Directory { get; }

    /// <summary>
    /// Ends the claim of every session but <paramref name="activeSessionId"/> on the links its downloads went
    /// through: none of them can be using those models any more.
    /// </summary>
    public void ReleaseSessionsOtherThan(string? activeSessionId)
    {
        var active = NormalizeSession(activeSessionId);
        lock (_gate)
        {
            foreach (var (path, sessionId) in _linkSessions.ToArray())
            {
                if (!string.Equals(sessionId, active, StringComparison.Ordinal))
                {
                    _linkSessions.Remove(path);
                }
            }
        }
    }

    /// <summary>Ends <paramref name="sessionId"/>'s claim on the links its downloads went through.</summary>
    public void ReleaseSession(string? sessionId)
    {
        var ended = NormalizeSession(sessionId);
        lock (_gate)
        {
            foreach (var (path, linkSessionId) in _linkSessions.ToArray())
            {
                if (string.Equals(linkSessionId, ended, StringComparison.Ordinal))
                {
                    _linkSessions.Remove(path);
                }
            }
        }
    }

    private static string? NormalizeSession(string? sessionId)
        => string.IsNullOrWhiteSpace(sessionId) ? null : sessionId.Trim();

    /// <summary>
    /// Whether <paramref name="editorPath"/> is a link a download was sent through for a session that has not
    /// ended, still pointing at that download: the projector, whose links point into the same folder, must
    /// leave it, since a prompt may be waiting for the model or using it.
    /// </summary>
    public bool KeepsEditorLink(string editorPath)
    {
        var normalized = Normalize(editorPath);
        if (!IsStrictlyBeneath(normalized, _editorModelsDirectory))
        {
            return false;
        }

        var relative = Path.GetRelativePath(_editorModelsDirectory, normalized);
        lock (_gate)
        {
            return _linkSessions.ContainsKey(relative.Replace(Path.DirectorySeparatorChar, '/')) &&
                   PointsAtLibrary(relative, Path.Combine(_readOnlyDirectory, relative));
        }
    }

    /// <summary>
    /// Runs <paramref name="change"/> under the lock every redirect takes to claim, make or remove a link.
    /// </summary>
    public void WhileLinksAreHeld(Action change)
    {
        lock (_gate)
        {
            change();
        }
    }

    /// <summary>
    /// Sends each remote request for the editor's models directory to the same relative path in the models
    /// folder, and links the editor's path there. A request is left as it was, and downloaded inside the
    /// container as before, when something already holds either place: a file of the owner's of another
    /// size, or a link or file at the editor's path. A file of the requested size there counts as the model.
    /// </summary>
    /// <param name="sessionId">
    /// The session asking. A link this library made to another model at the same path gives way only when
    /// another session asked for that model: the session that did may still be using it, so it is refused a
    /// model of another size there, as two models at one path always are.
    /// </param>
    public ModelDownloadRequest[] Redirect(IEnumerable<ModelDownloadRequest> downloads, string? sessionId)
    {
        // Prompts are prepared concurrently: two naming the same new model must not both find its link
        // missing, and the second fall back to downloading over the first one's link.
        var session = NormalizeSession(sessionId);
        lock (_gate)
        {
            // A refused request fails the whole batch, and nothing of it is registered: the links and claims
            // made for the requests before it are undone, so none keeps a path from a local model.
            var claims = new Dictionary<string, string?>(_linkSessions, StringComparer.Ordinal);
            _linksMadeInBatch = [];
            try
            {
                return downloads.Select(download => Redirect(download, session)).ToArray();
            }
            catch
            {
                UndoBatch(claims);
                throw;
            }
            finally
            {
                _linksMadeInBatch = null;
            }
        }
    }

    private void UndoBatch(Dictionary<string, string?> claimsBefore)
    {
        foreach (var relative in _linksMadeInBatch!)
        {
            if (HoldsOwnLink(relative, Path.Combine(_readOnlyDirectory, relative)))
            {
                DetachEditorLink(relative);
            }
        }

        // A link removed meanwhile keeps no claim.
        foreach (var path in _linkSessions.Keys.ToArray())
        {
            if (claimsBefore.TryGetValue(path, out var sessionId))
            {
                _linkSessions[path] = sessionId;
            }
            else
            {
                _linkSessions.Remove(path);
            }
        }
    }

    public void SetProjected(string editorPath, bool projected)
    {
        var normalized = Normalize(editorPath);
        if (!IsStrictlyBeneath(normalized, _editorModelsDirectory))
        {
            return;
        }

        var key = Path.GetRelativePath(_editorModelsDirectory, normalized).Replace(Path.DirectorySeparatorChar, '/');
        lock (_gate)
        {
            if (projected)
            {
                _projectedLinks.Add(key);
            }
            else
            {
                _projectedLinks.Remove(key);
            }
        }
    }

    private ModelDownloadRequest Redirect(ModelDownloadRequest download, string? sessionId)
    {
        if (LocalModelProjector.IsLocalProjectionRequest(download) ||
            string.IsNullOrWhiteSpace(download.DestinationPath) ||
            download.SizeBytes <= 0)
        {
            return download;
        }

        var editorPath = Normalize(download.DestinationPath);
        if (!IsStrictlyBeneath(editorPath, _editorModelsDirectory))
        {
            return download;
        }

        var relative = Path.GetRelativePath(_editorModelsDirectory, editorPath);
        var libraryPath = Path.Combine(Directory, relative);
        var linkTarget = Path.Combine(_readOnlyDirectory, relative);
        if (IsInDownloadFolder(libraryPath))
        {
            return download;
        }

        if (!HasRealDirectoriesUpTo(libraryPath) || !IsFreeOrSameSize(libraryPath, download.SizeBytes))
        {
            // Another model is saved there. While the editor's path links to it for this session, as its
            // download or as a local model the projector linked, it may be in use, and this one is refused.
            // Otherwise a link this library made goes, so this one can be downloaded inside the container at
            // that path.
            var key = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (PointsAtLibrary(relative, linkTarget))
            {
                if (_projectedLinks.Contains(key) ||
                    (_linkSessions.TryGetValue(key, out var linkSessionId) &&
                     string.Equals(linkSessionId, sessionId, StringComparison.Ordinal)))
                {
                    throw new InvalidOperationException(
                        $"Model '{relative}' cannot be downloaded in this session: another model of the same name, saved in the models folder, is already in use at that path.");
                }

                if (_ownLinks.Contains(key))
                {
                    DetachEditorLink(relative);
                }
            }

            return download;
        }

        if (!TryLinkEditorPath(relative, linkTarget, sessionId))
        {
            return download;
        }

        var redirected = download.Clone();
        redirected.DestinationPath = libraryPath;
        return redirected;
    }

    /// <summary>
    /// Whether every folder that exists between the models folder and <paramref name="path"/> is a real one,
    /// on the folder's own mount: a download is published only through real directories, an owner's link (a
    /// <c>checkpoints</c> folder on another drive, say) is never written through, and a download staged in
    /// the folder can be moved into place only on the mount it was staged on (a folder mounted there from
    /// another filesystem, or bound there from elsewhere on the same one, cannot take it).
    /// </summary>
    private bool HasRealDirectoriesUpTo(string path)
    {
        if (!LinuxFileIdentityReader.TryReadMount(Directory, out var folder) ||
            !IsOnMount(_downloadFolder, folder) ||
            !IsOnMount(_stagingDirectory, folder))
        {
            return false;
        }

        var current = Directory;
        foreach (var part in Path.GetRelativePath(Directory, Path.GetDirectoryName(path)!)
                     .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                     .Where(static part => part != "."))
        {
            current = Path.Combine(current, part);
            var entry = new DirectoryInfo(current);
            if (entry.LinkTarget is not null || File.Exists(current) || !IsOnMount(current, folder))
            {
                return false;
            }

            if (!entry.Exists)
            {
                // The rest is made when the model is published.
                return true;
            }
        }

        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is missing, or on <paramref name="mount"/>: the same device, and the same
    /// mount where the kernel tells mounts apart.
    /// </summary>
    private static bool IsOnMount(string path, (ulong DeviceId, ulong? MountId) mount)
        => !LinuxFileIdentityReader.TryReadMount(path, out var entry)
            ? !Path.Exists(path)
            : entry.DeviceId == mount.DeviceId && (entry.MountId is null || mount.MountId is null || entry.MountId == mount.MountId);

    private static bool IsFreeOrSameSize(string path, long size)
    {
        var existing = new FileInfo(path);
        if (existing.LinkTarget is not null || System.IO.Directory.Exists(path))
        {
            // A link, or a folder of the owner's of that name: the place is taken.
            return false;
        }

        return !existing.Exists || existing.Length == size;
    }

    /// <summary>Whether the editor's path holds a link this library made (the projector links to the same place).</summary>
    private bool HoldsOwnLink(string relative, string target)
        => _ownLinks.Contains(relative.Replace(Path.DirectorySeparatorChar, '/')) && PointsAtLibrary(relative, target);

    /// <summary>Whether the editor's path is a link to <paramref name="target"/>.</summary>
    private bool PointsAtLibrary(string relative, string target)
    {
        if (!System.IO.Directory.Exists(_editorModelsDirectory))
        {
            return false;
        }

        try
        {
            using var root = new LinuxSecureDirectoryRoot(_editorModelsDirectory);
            return root.ReadSymbolicLink(relative.Replace(Path.DirectorySeparatorChar, '/'), out var existing) == SecureLinkKind.SymbolicLink &&
                   string.Equals(existing, target, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gives up the editor's link to a download that was never saved: removed if this library made it, and no
    /// longer kept from the projector otherwise.
    /// </summary>
    private void ReleaseEditorLink(string relative)
    {
        lock (_gate)
        {
            if (HoldsOwnLink(relative, Path.Combine(_readOnlyDirectory, relative)))
            {
                DetachEditorLink(relative);
            }

            _linkSessions.Remove(relative.Replace(Path.DirectorySeparatorChar, '/'));
        }
    }

    private void DetachEditorLink(string relative)
    {
        var relativePath = relative.Replace(Path.DirectorySeparatorChar, '/');
        try
        {
            using var root = new LinuxSecureDirectoryRoot(_editorModelsDirectory);
            root.DeleteSymbolicLink(relativePath);
            _ownLinks.Remove(relativePath);
            _linkSessions.Remove(relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not remove the link at '{relative}' ({ex.GetType().Name}).");
        }
    }

    private bool TryLinkEditorPath(string relative, string target, string? sessionId)
    {
        try
        {
            System.IO.Directory.CreateDirectory(_editorModelsDirectory);
            using var root = new LinuxSecureDirectoryRoot(_editorModelsDirectory);
            var relativePath = relative.Replace(Path.DirectorySeparatorChar, '/');
            switch (root.ReadSymbolicLink(relativePath, out var existing))
            {
                case SecureLinkKind.SymbolicLink:
                    // Ours already, or another to the same place (a local model's, or one an earlier run made):
                    // either way, nothing to change, except that this session's download now goes through it.
                    if (!string.Equals(existing, target, StringComparison.Ordinal))
                    {
                        return false;
                    }

                    _linkSessions[relativePath] = sessionId;
                    return true;
                case SecureLinkKind.Other:
                    // A model downloaded inside the container earlier, or a path that is not plain directories.
                    return false;
            }

            root.CreateSymbolicLink(relativePath, target);
            _ownLinks.Add(relativePath);
            _linksMadeInBatch?.Add(relativePath);
            _linkSessions[relativePath] = sessionId;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not link '{relative}' to the models folder ({ex.GetType().Name}); it is downloaded inside the container.");
            return false;
        }
    }

    public string? GetStagingPath(string destinationPath)
    {
        var destination = Normalize(destinationPath);
        if (!IsStrictlyBeneath(destination, Directory) || IsInDownloadFolder(destination))
        {
            return null;
        }

        // Keep the file name: a Metalink names the file it describes.
        return Path.Combine(_stagingDirectory, StagingKey(destination), Path.GetFileName(destination));
    }

    public void Publish(string stagingPath, string destinationPath)
    {
        var destination = Normalize(destinationPath);
        var expected = GetStagingPath(destination);
        if (expected is null || !string.Equals(expected, Normalize(stagingPath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"'{stagingPath}' is not the staging file for '{destination}'.");
        }

        bool moved;
        using (var root = new LinuxSecureDirectoryRoot(Directory))
        {
            moved = root.MoveRegularFileWithoutReplacing(
                Path.GetRelativePath(Directory, expected).Replace(Path.DirectorySeparatorChar, '/'),
                Path.GetRelativePath(Directory, destination).Replace(Path.DirectorySeparatorChar, '/'));
        }

        if (!moved)
        {
            // Something was put there meanwhile: another runner sharing the folder saved the same model, or
            // the owner copied it in. Of the same size it is that model, and the download is done; of another
            // size it is the owner's, and it stays.
            var staged = new FileInfo(expected).Length;
            var there = new FileInfo(destination);
            Discard(destination);
            if (there.Exists && there.LinkTarget is null && there.Length == staged)
            {
                return;
            }

            // The editor's link would now open the owner's file under this model's name.
            ReleaseEditorLink(Path.GetRelativePath(Directory, destination));
            throw new IOException($"A file appeared at '{Path.GetRelativePath(Directory, destination)}' in the models folder while it was downloaded; it is kept.");
        }

        DeleteStagingDirectory(Path.GetDirectoryName(expected)!);
    }

    public void Discard(string destinationPath)
    {
        var stagingPath = GetStagingPath(destinationPath);
        if (stagingPath is not null)
        {
            DeleteStagingDirectory(Path.GetDirectoryName(stagingPath)!);
        }
    }

    /// <summary>Returns true for files aria2 writes beneath the folder's download area.</summary>
    public bool IsStagingPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsStrictlyBeneath(Normalize(path), _stagingDirectory);

    /// <summary>Removes what an earlier run of this runner left half downloaded; other runners' stay.</summary>
    public void DiscardAll() => DeleteStagingDirectory(_stagingDirectory, keepDirectory: true);

    /// <summary>
    /// Deletes a folder in the download folder. Others sharing the models folder can change it, so the
    /// deletion goes through no link: the download folder, this runner's folder and everything in them are
    /// opened without following links, and a folder swapped for one meanwhile is left alone.
    /// </summary>
    private void DeleteStagingDirectory(string directory, bool keepDirectory = false)
    {
        try
        {
            using var root = new LinuxSecureDirectoryRoot(Directory);
            root.DeleteDirectoryTree(Path.GetRelativePath(Directory, directory).Replace(Path.DirectorySeparatorChar, '/'), keepDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not remove downloaded part '{directory}': {ex.Message}");
        }
    }

    /// <summary>
    /// Whether a folder of this name at the top of the models folder is the download folder. On a
    /// case-insensitive share (a Windows folder) it is, in any case.
    /// </summary>
    public static bool IsStagingFolderName(string name)
        => string.Equals(name, StagingFolderName, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether <paramref name="path"/> is inside the download folder, in whatever case it is named.</summary>
    private bool IsInDownloadFolder(string path)
        => IsStrictlyBeneath(path, Directory) &&
           IsStagingFolderName(Path.GetRelativePath(Directory, path).Split(Path.DirectorySeparatorChar)[0]);

    private static string StagingKey(string destination)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))[..32];

    private static bool IsStrictlyBeneath(string path, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.Length > prefix.Length && path.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}

/// <summary>Stages a download in a session home or in the models folder, whichever its destination is in.</summary>
internal sealed class CombinedStaging(IModelDownloadStaging? sessionStaging, ModelLibrary library) : IModelDownloadStaging
{
    public string? GetStagingPath(string destinationPath)
        => library.GetStagingPath(destinationPath) ?? sessionStaging?.GetStagingPath(destinationPath);

    public void Publish(string stagingPath, string destinationPath)
    {
        if (library.GetStagingPath(destinationPath) is not null)
        {
            library.Publish(stagingPath, destinationPath);
            return;
        }

        (sessionStaging ?? throw new InvalidOperationException($"'{destinationPath}' is not staged.")).Publish(stagingPath, destinationPath);
    }

    public void Discard(string destinationPath)
    {
        library.Discard(destinationPath);
        sessionStaging?.Discard(destinationPath);
    }
}

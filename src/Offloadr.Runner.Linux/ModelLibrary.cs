using Microsoft.Win32.SafeHandles;
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
internal sealed class ModelLibrary : IModelDownloadStaging, IDownloadLinks, IDisposable
{
    /// <summary>Where downloads in progress are kept, inside the models folder.</summary>
    public const string StagingFolderName = ".offloadr-downloads";

    private readonly string _readOnlyDirectory;
    private readonly string _editorModelsDirectory;
    private readonly string _downloadFolder;
    private readonly string _runnerDirectory;
    private readonly string _instanceName;
    private readonly string _stagingDirectory;
    private readonly object _leaseGate = new();
    private SafeFileHandle? _lease;
    private bool _leaseTaken;
    private readonly object _gate = new();
    // Editor links this library made, under _gate: the only ones it ever removes.
    private readonly HashSet<string> _ownLinks = new(StringComparer.Ordinal);
    // Editor links a download was sent through (made here, or found in place), under _gate, each with the
    // session that last asked for its model: until that session ends it may be using the model.
    private readonly Dictionary<string, string?> _linkSessions = new(StringComparer.Ordinal);
    // Sessions that have ended, under _gate: a prompt of one still finishing claims nothing for it. And the
    // session last made active, whose end is seen when another becomes active.
    private readonly HashSet<string> _endedSessions = new(StringComparer.Ordinal);
    // The size of the model each claim on a link was made for, under _gate: a download that cannot be saved
    // gives up only a claim made for it, not one made for the owner's file of another size there.
    private readonly Dictionary<string, long> _claimSizes = new(StringComparer.Ordinal);
    private string? _activeSession;
    // The redirect that last claimed each of those links, under _gate: undoing another redirect leaves it.
    private readonly Dictionary<string, RedirectBatch> _claimedBy = new(StringComparer.Ordinal);
    // Editor paths where the projector links a local model for the session now, under _gate.
    private readonly HashSet<string> _projectedLinks = new(StringComparer.Ordinal);
    // What the redirect in progress changes, under _gate.
    private RedirectBatch? _batch;

    /// <param name="directory">The models folder, writable.</param>
    /// <param name="readOnlyDirectory">The same folder as sessions see it, read-only.</param>
    /// <param name="editorModelsDirectory">Where the editor finds its models: requests for paths there are sent to the folder.</param>
    /// <param name="runnerId">
    /// This runner's id. Several runners may share one folder, so each keeps its downloads in progress in a
    /// folder of its own and clears only that one.
    /// </param>
    /// <param name="instanceId">
    /// This run's id: two runs of one runner may overlap (a relaunch), so each stages in a folder of its own,
    /// leased for as long as it runs, and clears only those of runs that have ended.
    /// </param>
    public ModelLibrary(string directory, string readOnlyDirectory, string editorModelsDirectory, string runnerId, string? instanceId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(readOnlyDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(editorModelsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerId);
        Directory = Normalize(directory);
        _readOnlyDirectory = Normalize(readOnlyDirectory);
        _editorModelsDirectory = Normalize(editorModelsDirectory);
        _downloadFolder = Path.Combine(Directory, StagingFolderName);
        _runnerDirectory = Path.Combine(_downloadFolder, RunnerFolderName(runnerId));
        _instanceName = string.IsNullOrWhiteSpace(instanceId) ? Guid.NewGuid().ToString("N") : RunnerFolderName(instanceId);
        _stagingDirectory = Path.Combine(_runnerDirectory, _instanceName);
    }

    private const string LeaseSuffix = ".lease";

    /// <summary>Gives up this run's lease: its downloads in progress are then another run's to clear.</summary>
    public void Dispose()
    {
        lock (_leaseGate)
        {
            _lease?.Dispose();
            _lease = null;
        }
    }

    /// <summary>
    /// Takes this run's lease, once: a lock on <c>&lt;instance&gt;.lease</c> beside its folder, taken before the
    /// folder is made, so a run clearing ended ones never takes a live run's folder for an ended one's.
    /// </summary>
    private void EnsureLease()
    {
        lock (_leaseGate)
        {
            if (_leaseTaken)
            {
                return;
            }

            using var root = new LinuxSecureDirectoryRoot(Directory);
            root.EnsureDirectoryInheritingOwner(RelativeToFolder(_runnerDirectory));
            switch (root.TryLockFile(RelativeToFolder(_stagingDirectory + LeaseSuffix), out var lease))
            {
                case SecureLockResult.Locked:
                    _lease = lease;
                    break;
                case SecureLockResult.Unsupported:
                    RunnerLog.Warning(nameof(ModelLibrary), "The models folder takes no file locks: an overlapping run of this runner may clear this run's downloads in progress.");
                    break;
                default:
                    throw new IOException($"This run's download folder lease is held by another process.");
            }

            _leaseTaken = true;
        }
    }

    private string RelativeToFolder(string path)
        => Path.GetRelativePath(Directory, path).Replace(Path.DirectorySeparatorChar, '/');

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
            // The session that was active has ended, unless it is the one active again.
            if (_activeSession is { } previous && !string.Equals(previous, active, StringComparison.Ordinal))
            {
                _endedSessions.Add(previous);
            }

            _activeSession = active;
            if (active is not null)
            {
                _endedSessions.Remove(active);
            }

            foreach (var (path, sessionId) in _linkSessions.ToArray())
            {
                if (!string.Equals(sessionId, active, StringComparison.Ordinal))
                {
                    _linkSessions.Remove(path);
                    _claimedBy.Remove(path);
                    if (sessionId is not null)
                    {
                        _endedSessions.Add(sessionId);
                    }
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
            if (ended is not null)
            {
                _endedSessions.Add(ended);
            }

            foreach (var (path, linkSessionId) in _linkSessions.ToArray())
            {
                if (string.Equals(linkSessionId, ended, StringComparison.Ordinal))
                {
                    _linkSessions.Remove(path);
                    _claimedBy.Remove(path);
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
        var redirected = Redirect(downloads, sessionId, out var batch);
        Commit(batch);
        return redirected;
    }

    /// <param name="batch">
    /// What this redirect changed, for <see cref="Undo"/> when the batch is refused later on, before anything
    /// of it is registered, or <see cref="Commit"/> once it is registered.
    /// </param>
    public ModelDownloadRequest[] Redirect(IEnumerable<ModelDownloadRequest> downloads, string? sessionId, out RedirectBatch batch)
    {
        // Prompts are prepared concurrently: two naming the same new model must not both find its link
        // missing, and the second fall back to downloading over the first one's link.
        var session = NormalizeSession(sessionId);
        batch = new RedirectBatch(session);
        lock (_gate)
        {
            // A prompt of a session that ended while it waited here changes no link: the next session's
            // links may already be at its paths. It is stopped, and downloads nothing into the folder.
            if (session is not null && _endedSessions.Contains(session))
            {
                return downloads.ToArray();
            }

            // A refused request fails the whole batch, and nothing of it is registered: the links and claims
            // made for the requests before it are undone, so none keeps a path from a local model.
            _batch = batch;
            try
            {
                return downloads.Select(download => Redirect(download, session)).ToArray();
            }
            catch
            {
                UndoLocked(batch);
                throw;
            }
            finally
            {
                _batch = null;
            }
        }
    }

    /// <summary>
    /// Undoes the links and claims <paramref name="batch"/> made, where they are still as it left them: the
    /// batch was refused after it was redirected, and none of its downloads is registered.
    /// </summary>
    public void Undo(RedirectBatch batch)
    {
        lock (_gate)
        {
            UndoLocked(batch);
        }
    }

    /// <summary>
    /// Settles <paramref name="batch"/> once its downloads are registered: it can no longer be undone, and its
    /// claims stand as committed ones. Nothing keeps the batch, or those before it, from then on.
    /// </summary>
    public void Commit(RedirectBatch batch)
    {
        lock (_gate)
        {
            foreach (var path in batch.ClaimsBefore.Keys)
            {
                if (_claimedBy.TryGetValue(path, out var by) && ReferenceEquals(by, batch))
                {
                    _claimedBy[path] = RedirectBatch.Committed;
                }
            }

            batch.ClaimsBefore.Clear();
            batch.LinksMade.Clear();
        }
    }

    private void UndoLocked(RedirectBatch batch)
    {
        batch.Undone = true;

        // Another redirect, of this session or another, may have sent its download through a link since: then
        // the link is that redirect's as well, and it stays with its claim. So does one the projector now links a
        // local model at.
        foreach (var relative in batch.LinksMade)
        {
            DetachUnlessInUse(relative, batch);
        }

        // A link removed meanwhile keeps no claim.
        foreach (var (path, claimBefore) in batch.ClaimsBefore)
        {
            if (ClaimedByAnother(path, batch) || !_linkSessions.ContainsKey(path))
            {
                continue;
            }

            // A claim set by a redirect undone since is no claim: go back to the one before it. If such a
            // redirect made the link, nothing that was kept sent a download through it, and it goes.
            var before = claimBefore;
            RedirectBatch? undoneMaker = null;
            while (before.By is { Undone: true } undone && undone.ClaimsBefore.TryGetValue(path, out var earlier))
            {
                if (undone.LinksMade.Contains(path))
                {
                    undoneMaker = undone;
                }

                before = earlier;
            }

            // Nor does a claim of a session that has ended since come back.
            if (before.Existed && before.By is not { Undone: true } &&
                (before.SessionId is null || !_endedSessions.Contains(before.SessionId)))
            {
                _linkSessions[path] = before.SessionId;
                SetClaimedBy(path, before.By);
                _claimSizes[path] = before.Size;
            }
            else
            {
                _linkSessions.Remove(path);
                _claimedBy.Remove(path);
                if (undoneMaker is not null)
                {
                    DetachUnlessInUse(path, batch);
                }
            }
        }
    }

    /// <summary>Removes a link a refused redirect made, unless another claimed it or the projector links there.</summary>
    private void DetachUnlessInUse(string relative, RedirectBatch batch)
    {
        if (!ClaimedByAnother(relative, batch) &&
            !_projectedLinks.Contains(relative.Replace(Path.DirectorySeparatorChar, '/')) &&
            HoldsOwnLink(relative, Path.Combine(_readOnlyDirectory, relative)))
        {
            DetachEditorLink(relative);
        }
    }

    private bool ClaimedByAnother(string relativePath, RedirectBatch batch)
        => _claimedBy.TryGetValue(relativePath, out var by) && !ReferenceEquals(by, batch);

    private void SetClaimedBy(string relativePath, RedirectBatch? batch)
    {
        if (batch is null)
        {
            _claimedBy.Remove(relativePath);
        }
        else
        {
            _claimedBy[relativePath] = batch;
        }
    }

    private void Claim(string relativePath, string? sessionId, long size)
    {
        // A prompt of an ended session, stopped while it waited here, keeps no link from the session after.
        if (sessionId is not null && _endedSessions.Contains(sessionId))
        {
            return;
        }

        if (_batch is { } batch)
        {
            batch.ClaimsBefore.TryAdd(
                relativePath,
                _linkSessions.TryGetValue(relativePath, out var before)
                    ? (true, before, _claimedBy.GetValueOrDefault(relativePath), _claimSizes.GetValueOrDefault(relativePath))
                    : (false, null, null, 0));
        }

        _linkSessions[relativePath] = sessionId;
        SetClaimedBy(relativePath, _batch);
        _claimSizes[relativePath] = size;
    }

    /// <summary>What one redirect changed: the links it made, and the claims it set, with what they were.</summary>
    internal sealed class RedirectBatch(string? sessionId)
    {
        /// <summary>Stands for every committed batch: what it claimed is registered, and no undo takes it.</summary>
        public static RedirectBatch Committed { get; } = new(null);

        public string? SessionId { get; } = sessionId;

        /// <summary>Whether the batch was refused and undone: the claims it set stand for nothing.</summary>
        public bool Undone { get; set; }

        public List<string> LinksMade { get; } = [];

        public Dictionary<string, (bool Existed, string? SessionId, RedirectBatch? By, long Size)> ClaimsBefore { get; } = new(StringComparer.Ordinal);
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

        if (!TryLinkEditorPath(relative, linkTarget, sessionId, download.SizeBytes))
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
        // The download folders too: a download is staged only through real ones, never through a link.
        if (!LinuxFileIdentityReader.TryReadMount(Directory, out var folder) ||
            !IsMissingOrRealDirectory(_downloadFolder) ||
            !IsMissingOrRealDirectory(_runnerDirectory) ||
            !IsMissingOrRealDirectory(_stagingDirectory) ||
            !IsOnMount(_downloadFolder, folder) ||
            !IsOnMount(_runnerDirectory, folder) ||
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
    private static bool IsMissingOrRealDirectory(string path)
    {
        var entry = new DirectoryInfo(path);
        return entry.LinkTarget is null && (entry.Exists || !File.Exists(path));
    }

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
    /// <param name="size">The size of the download: only a claim made for a model of that size is given up.</param>
    private void ReleaseEditorLink(string relative, long size)
    {
        lock (_gate)
        {
            // A request for the owner's file, of its own size, claimed the link: it is that request's now.
            var key = relative.Replace(Path.DirectorySeparatorChar, '/');
            if (_linkSessions.ContainsKey(key) && _claimSizes.TryGetValue(key, out var claimed) && claimed != size)
            {
                return;
            }

            // Unless the projector now links the owner's file there as the local model the session asked for.
            if (!_projectedLinks.Contains(relative.Replace(Path.DirectorySeparatorChar, '/')) &&
                HoldsOwnLink(relative, Path.Combine(_readOnlyDirectory, relative)))
            {
                DetachEditorLink(relative);
            }

            _linkSessions.Remove(relative.Replace(Path.DirectorySeparatorChar, '/'));
            _claimedBy.Remove(relative.Replace(Path.DirectorySeparatorChar, '/'));
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
            _claimedBy.Remove(relativePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not remove the link at '{relative}' ({ex.GetType().Name}).");
        }
    }

    private bool TryLinkEditorPath(string relative, string target, string? sessionId, long size)
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

                    Claim(relativePath, sessionId, size);
                    return true;
                case SecureLinkKind.Other:
                    // A model downloaded inside the container earlier, or a path that is not plain directories.
                    return false;
            }

            root.CreateSymbolicLink(relativePath, target);
            _ownLinks.Add(relativePath);
            _batch?.LinksMade.Add(relativePath);
            Claim(relativePath, sessionId, size);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not link '{relative}' to the models folder ({ex.GetType().Name}); it is downloaded inside the container.");
            return false;
        }
    }

    /// <summary>
    /// Makes the folders a download is staged in, each the owner's of the folder it is made in, as published
    /// models are: the owner can remove what a runner that never comes back leaves half downloaded.
    /// </summary>
    public void PrepareStagingDirectory(string stagingPath)
    {
        var normalized = Normalize(stagingPath);
        if (!IsStrictlyBeneath(normalized, _stagingDirectory))
        {
            throw new InvalidOperationException($"'{stagingPath}' is not in this runner's download folder.");
        }

        EnsureLease();
        using var root = new LinuxSecureDirectoryRoot(Directory);
        root.EnsureDirectoryInheritingOwner(
            Path.GetRelativePath(Directory, Path.GetDirectoryName(normalized)!).Replace(Path.DirectorySeparatorChar, '/'));
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

        var relativeDestination = Path.GetRelativePath(Directory, destination);
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
            ReleaseEditorLink(relativeDestination, staged);
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

    /// <summary>
    /// Whether <paramref name="path"/> is a download in progress of this run or of an ended run of this runner,
    /// as aria2 restores them from its saved state after a restart: those are given up before
    /// <see cref="DiscardAll"/> clears their folders. A run still going, whose lease is held, keeps its own.
    /// </summary>
    public bool IsStagingPathOfThisOrAnEndedRun(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = Normalize(path);
        if (IsStrictlyBeneath(normalized, _stagingDirectory))
        {
            return true;
        }

        if (!IsStrictlyBeneath(normalized, _runnerDirectory))
        {
            return false;
        }

        var run = Path.GetRelativePath(_runnerDirectory, normalized).Split(Path.DirectorySeparatorChar)[0];
        if (run.EndsWith(LeaseSuffix, StringComparison.Ordinal) || !IsMissingOrRealDirectory(_runnerDirectory))
        {
            return false;
        }

        try
        {
            using var root = new LinuxSecureDirectoryRoot(Directory);
            var result = root.TryLockFile(RelativeToFolder(Path.Combine(_runnerDirectory, run)) + LeaseSuffix, out var lease);
            lease?.Dispose();
            return result != SecureLockResult.Held;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Removes what an earlier run of this runner left half downloaded; other runners' stay.</summary>
    public void DiscardAll()
    {
        DeleteStagingDirectory(_stagingDirectory, keepDirectory: true);

        // And what ended runs of this runner left: a run still going holds its lease, and keeps its folder.
        // Where the folder takes no locks, no run can be told from an ended one, and all are cleared as ever.
        string[] entries;
        try
        {
            if (!IsMissingOrRealDirectory(_downloadFolder) || !IsMissingOrRealDirectory(_runnerDirectory) ||
                !System.IO.Directory.Exists(_runnerDirectory))
            {
                return;
            }

            entries = System.IO.Directory.GetFileSystemEntries(_runnerDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not list earlier downloads in progress: {ex.Message}");
            return;
        }

        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var run = name.EndsWith(LeaseSuffix, StringComparison.Ordinal) ? name[..^LeaseSuffix.Length] : name;
            if (string.Equals(run, _instanceName, StringComparison.Ordinal) ||
                (name != run && entries.Contains(Path.Combine(_runnerDirectory, run), StringComparer.Ordinal)))
            {
                // This run's own, or a lease handled with its folder.
                continue;
            }

            ClearEndedRun(run);
        }
    }

    private void ClearEndedRun(string run)
    {
        var folder = RelativeToFolder(Path.Combine(_runnerDirectory, run));
        var lease = folder + LeaseSuffix;
        try
        {
            using var root = new LinuxSecureDirectoryRoot(Directory);
            var result = root.TryLockFile(lease, out var handle);
            using (handle)
            {
                if (result == SecureLockResult.Held)
                {
                    return;
                }

                root.DeleteDirectoryTree(folder);
                root.DeleteFile(lease);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(ModelLibrary), $"Could not remove an earlier run's downloads in progress: {ex.Message}");
        }
    }

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

    public void PrepareStagingDirectory(string stagingPath)
    {
        if (library.IsStagingPath(stagingPath))
        {
            library.PrepareStagingDirectory(stagingPath);
        }
        else
        {
            sessionStaging?.PrepareStagingDirectory(stagingPath);
        }
    }

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

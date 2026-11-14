using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Projects registered local models into the editor's model directory as symbolic links.
/// The periodic inventory scan and the start/prompt command handlers call in from different
/// threads, so every public entry point holds <see cref="_gate"/> for the whole update,
/// including its filesystem work.
/// </summary>
internal sealed class LocalModelProjector
{
    private readonly object _gate = new();
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;
    private readonly Dictionary<string, ModelInfo> _modelsBySelectionHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ModelInfo>> _modelsByNormalizedFilename = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalProjectionRequest> _desiredProjections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _managedTargets = new(StringComparer.Ordinal);
    private const int MaxDiscoveryEntries = 200_000;
    private readonly IDownloadLinks? _downloadLinks;
    private bool _discoveredEarlierProjections;

    /// <param name="downloadLinks">
    /// The links of downloads saved in the local folder: they point into the source root as projections do,
    /// and are left in place while a session may use them.
    /// </param>
    public LocalModelProjector(
        string sourceRoot,
        string destinationRoot = "/comfyui/models",
        string runtimeKind = "comfyui",
        IDownloadLinks? downloadLinks = null)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
        {
            throw new ArgumentException("Source root is required.", nameof(sourceRoot));
        }

        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            throw new ArgumentException("Destination root is required.", nameof(destinationRoot));
        }

        _sourceRoot = NormalizePath(sourceRoot);
        _destinationRoot = NormalizePath(destinationRoot);
        _downloadLinks = downloadLinks;
        _ = runtimeKind;
    }

    public void UpdateSnapshot(LocalModelSnapshot snapshot)
    {
        lock (_gate)
        {
            _modelsBySelectionHash.Clear();
            _modelsByNormalizedFilename.Clear();
            var ambiguous = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in snapshot.Models)
            {
                var normalizedFilename = LocalModelSelectionHash.NormalizeFileName(model.Filename);
                if (normalizedFilename.Length > 0)
                {
                    if (!_modelsByNormalizedFilename.TryGetValue(normalizedFilename, out var filenameMatches))
                    {
                        filenameMatches = [];
                        _modelsByNormalizedFilename.Add(normalizedFilename, filenameMatches);
                    }

                    filenameMatches.Add(model);
                }

                var selectionHash = ComputeSelectionHash(model);
                if (selectionHash is null)
                {
                    continue;
                }

                if (!_modelsBySelectionHash.TryAdd(selectionHash, model))
                {
                    ambiguous.Add(selectionHash);
                }
            }

            // Several files with one selection hash cannot be told apart by a request, so
            // none of them is projected; the scanner does not offer such hashes either.
            foreach (var selectionHash in ambiguous)
            {
                _modelsBySelectionHash.Remove(selectionHash);
            }

            ReconcileDesiredProjections();
        }
    }

    public void SetRequestedModels(IEnumerable<ModelDownloadRequest> downloads)
    {
        lock (_gate)
        {
            _desiredProjections.Clear();
            AddProjectionRequests(downloads, reportRemoteFallbacks: false);
            ReconcileDesiredProjections();
        }
    }

    public void AddRequestedModels(IEnumerable<ModelDownloadRequest> downloads)
    {
        lock (_gate)
        {
            AddProjectionRequests(downloads, reportRemoteFallbacks: true);
            ReconcileDesiredProjections();
        }
    }

    public static bool IsLocalProjectionRequest(ModelDownloadRequest? download)
        => !string.IsNullOrWhiteSpace(download?.LocalSelectionHash);

    /// <summary>
    /// Whether the local file <paramref name="download"/> names is in the current inventory and readable,
    /// so a link to it can be made now.
    /// </summary>
    public bool CanProject(ModelDownloadRequest download)
    {
        if (!IsLocalProjectionRequest(download))
        {
            return false;
        }

        // The checks AddProjectionRequests and EnsureProjectedLink make, so a request they would refuse
        // is refused here too.
        var selectionHash = LocalModelSelectionHash.NormalizeSelectionHash(download.LocalSelectionHash);
        if (!LocalModelSelectionHash.IsValidSelectionHash(selectionHash) ||
            string.IsNullOrWhiteSpace(download.DestinationPath) ||
            !IsUnderRoot(NormalizePath(download.DestinationPath), _destinationRoot) ||
            !IsReachableThroughRealDirectories(NormalizePath(download.DestinationPath)))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_modelsBySelectionHash.TryGetValue(selectionHash, out var model))
            {
                return false;
            }

            try
            {
                var sourcePath = BuildSourcePath(model);
                return !string.IsNullOrWhiteSpace(sourcePath) &&
                       File.Exists(sourcePath) &&
                       !IsSameFile(sourcePath, NormalizePath(download.DestinationPath));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// True when <paramref name="sourcePath"/> and <paramref name="targetPath"/> are one file, as when the
    /// local folder and the model folder are the same directory: freeing the target would delete the source.
    /// </summary>
    private static bool IsSameFile(string sourcePath, string targetPath)
    {
        if (PathsEqual(sourcePath, targetPath))
        {
            return true;
        }

        return OperatingSystem.IsLinux() &&
               LinuxFileIdentityReader.TryRead(Path.GetFullPath(LinuxPathCanonicalizer.ResolveExistingPath(sourcePath)), out var source) &&
               LinuxFileIdentityReader.TryRead(targetPath, out var target) &&
               source.DeviceId == target.DeviceId &&
               source.Inode == target.Inode;
    }

    /// <summary>
    /// Whether every directory between the destination root and <paramref name="targetPath"/> is a real
    /// one, as <see cref="EnsureProjectedLink"/> requires: a link there makes it refuse the target.
    /// </summary>
    private bool IsReachableThroughRealDirectories(string targetPath)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists(_destinationRoot))
        {
            return true;
        }

        try
        {
            using var secureRoot = new LinuxSecureDirectoryRoot(_destinationRoot);
            var relativePath = GetRelativeTargetPath(targetPath);
            return secureRoot.GetStatus(relativePath).Kind is SecureEntryKind.RegularFile or SecureEntryKind.Missing ||
                   secureRoot.ReadSymbolicLink(relativePath, out _) == SecureLinkKind.SymbolicLink;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private void AddProjectionRequests(
        IEnumerable<ModelDownloadRequest>? downloads,
        bool reportRemoteFallbacks)
    {
        if (downloads is null)
        {
            return;
        }

        foreach (var download in downloads)
        {
            if (!IsLocalProjectionRequest(download))
            {
                if (reportRemoteFallbacks)
                {
                    ReportRemoteFallback(download);
                }

                continue;
            }

            var selectionHash = LocalModelSelectionHash.NormalizeSelectionHash(download.LocalSelectionHash);
            if (!LocalModelSelectionHash.IsValidSelectionHash(selectionHash))
            {
                throw new InvalidOperationException($"Local model projection for '{download.Filename}' has an invalid selection hash.");
            }

            if (string.IsNullOrWhiteSpace(download.DestinationPath))
            {
                throw new InvalidOperationException($"Local model projection for '{download.Filename}' is missing a destination path.");
            }

            var targetPath = NormalizePath(download.DestinationPath);
            if (!IsUnderRoot(targetPath, _destinationRoot))
            {
                throw new InvalidOperationException($"Local model projection target '{targetPath}' escapes '{_destinationRoot}'.");
            }

            _desiredProjections[targetPath] = new LocalProjectionRequest(selectionHash, targetPath, download.Filename);
        }
    }

    private void ReportRemoteFallback(ModelDownloadRequest download)
    {
        var normalizedFilename = LocalModelSelectionHash.NormalizeFileName(download.Filename);
        if (normalizedFilename.Length == 0 ||
            !_modelsByNormalizedFilename.TryGetValue(normalizedFilename, out var filenameMatches) ||
            filenameMatches.Count == 0)
        {
            return;
        }

        var candidate = filenameMatches
            .OrderByDescending(model =>
                model.SizeBytes == download.SizeBytes && model.Category == download.Category)
            .ThenByDescending(model => model.Category == download.Category)
            .ThenByDescending(model => model.SizeBytes == download.SizeBytes)
            .ThenBy(static model => model.LocalPath, StringComparer.Ordinal)
            .First();
        var differences = new List<string>(2);
        if (candidate.SizeBytes != download.SizeBytes)
        {
            differences.Add($"size local={candidate.SizeBytes} catalog={download.SizeBytes}");
        }

        if (candidate.Category != download.Category)
        {
            differences.Add($"category local={candidate.Category} catalog={download.Category}");
        }

        var modelIdentity = string.IsNullOrWhiteSpace(download.ModelId)
            ? download.Filename
            : download.ModelId;
        if (differences.Count == 0)
        {
            RunnerLog.Warning<LocalModelProjector>(
                $"Local model candidate '{download.Filename}' matches catalog metadata but was not selected for catalog model '{modelIdentity}'. " +
                "Downloading the catalog version instead.");
            return;
        }

        RunnerLog.Warning<LocalModelProjector>(
            $"Local model candidate '{download.Filename}' was not selected for catalog model '{modelIdentity}': " +
            $"{string.Join(" and ", differences)}. Downloading the catalog version instead.");
    }

    /// <summary>
    /// Adopts links that an earlier agent process left in the destination tree, so they are
    /// reconciled like this process's own. Only the projector creates links from the
    /// destination into the local model root. The walk never enters a linked directory, and
    /// removal goes through the pinned destination root.
    /// </summary>
    private void DiscoverEarlierProjections()
    {
        if (_discoveredEarlierProjections)
        {
            return;
        }

        _discoveredEarlierProjections = true;
        if (!Directory.Exists(_destinationRoot))
        {
            return;
        }

        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true };
        var pending = new Stack<string>();
        pending.Push(_destinationRoot);
        var visited = 0;
        while (pending.Count > 0 && visited < MaxDiscoveryEntries)
        {
            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(pending.Pop()).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                visited++;
                if (entry.LinkTarget is { } linkTarget)
                {
                    var resolved = Path.GetFullPath(linkTarget, Path.GetDirectoryName(entry.FullName)!);
                    if (IsUnderRoot(resolved, _sourceRoot) && !IsKeptLink(NormalizePath(entry.FullName)))
                    {
                        _managedTargets.Add(NormalizePath(entry.FullName));
                    }
                }
                else if (entry is DirectoryInfo)
                {
                    pending.Push(entry.FullName);
                }
            }
        }
    }

    private void ReconcileDesiredProjections()
    {
        DiscoverEarlierProjections();
        var desiredTargets = new HashSet<string>(_desiredProjections.Keys, StringComparer.Ordinal);
        InvalidOperationException? failure = null;

        foreach (var projection in _desiredProjections.Values)
        {
            string? problem = null;
            string? sourcePath = null;
            if (!_modelsBySelectionHash.TryGetValue(projection.SelectionHash, out var model))
            {
                problem = $"Registered local model '{projection.Filename}' is no longer present in the runner local inventory.";
            }
            else
            {
                // BuildSourcePath also requires the file's real location inside the local root,
                // so a source swapped for a link to elsewhere fails here.
                sourcePath = BuildSourcePath(model);
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    problem = $"Registered local model '{projection.Filename}' is no longer readable at '{sourcePath}'.";
                }
            }

            if (problem is not null)
            {
                // A projection whose source is gone or no longer safe must not stay linked:
                // remove it now, and report the failure after the stale links are cleaned up.
                if (_managedTargets.Contains(projection.TargetPath))
                {
                    RemoveManagedLink(projection.TargetPath);
                }

                failure ??= new InvalidOperationException(problem);
                continue;
            }

            var targetPath = projection.TargetPath;
            if (PathsEqual(sourcePath!, targetPath))
            {
                continue;
            }

            try
            {
                EnsureProjectedLink(sourcePath!, targetPath);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                // The other models are still linked: a download may already have been given up for one of
                // them. The first failure is reported once they are done.
                failure ??= ex as InvalidOperationException ?? new InvalidOperationException(ex.Message, ex);
            }
        }

        foreach (var stale in _managedTargets.ToArray())
        {
            if (desiredTargets.Contains(stale))
            {
                continue;
            }

            RemoveManagedLink(stale);
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    private static string? ComputeSelectionHash(ModelInfo model)
    {
        try
        {
            return LocalModelSelectionHash.Compute(model.Filename, model.SizeBytes, model.Category);
        }
        catch
        {
            return null;
        }
    }

    private string? BuildSourcePath(ModelInfo model)
    {
        if (string.IsNullOrWhiteSpace(model.LocalPath) || string.IsNullOrWhiteSpace(model.Filename))
        {
            return null;
        }

        var localDirectory = NormalizePath(model.LocalPath);
        var sourcePath = Path.GetFullPath(Path.Combine(localDirectory, model.Filename));
        if (!IsUnderRoot(sourcePath, _sourceRoot))
        {
            return null;
        }

        // A link in the local tree could alias a host file outside it into the session, so
        // the file's real location must be inside the local model root as well.
        var canonicalSource = NormalizePath(LinuxPathCanonicalizer.ResolveExistingPath(sourcePath));
        var canonicalRoot = NormalizePath(LinuxPathCanonicalizer.ResolveExistingPath(_sourceRoot));
        if (!IsUnderRoot(canonicalSource, canonicalRoot))
        {
            return null;
        }

        return sourcePath;
    }

    private void EnsureProjectedLink(string sourcePath, string targetPath)
    {
        try
        {
            var relativePath = GetRelativeTargetPath(targetPath);

            // The destination root can contain session-writable directories (Forge
            // projects into session homes), so links are created and inspected
            // relative to the root without following symlinked components.
            Directory.CreateDirectory(_destinationRoot);
            using var secureRoot = new LinuxSecureDirectoryRoot(_destinationRoot);
            // A download may claim or make a link at this path meanwhile: the path is read and changed while
            // none can.
            WhileDownloadLinksAreHeld(() =>
            {
                switch (secureRoot.ReadSymbolicLink(relativePath, out var existingTarget))
                {
                    case SecureLinkKind.SymbolicLink when LinkPointsTo(targetPath, existingTarget!, sourcePath):
                        _managedTargets.Add(targetPath);
                        return;
                    // A link into the local root is a projection, or a download's link no session needs any
                    // more: either gives way to the model asked for now.
                    case SecureLinkKind.SymbolicLink when (_managedTargets.Contains(targetPath) || PointsIntoSource(targetPath, existingTarget!)) &&
                                                          !IsKeptLink(targetPath):
                        secureRoot.DeleteSymbolicLink(relativePath);
                        break;
                    case SecureLinkKind.SymbolicLink:
                        throw new InvalidOperationException($"Projection target '{targetPath}' already exists as an unmanaged symbolic link.");
                    case SecureLinkKind.Other:
                        throw new InvalidOperationException($"Projection target '{targetPath}' already exists or is not beneath real directories.");
                }

                secureRoot.CreateSymbolicLink(relativePath, sourcePath);
                _managedTargets.Add(targetPath);
            });
        }
        catch (Exception ex)
        {
            RunnerLog.Error<LocalModelProjector>(ex, $"Failed to project local model '{sourcePath}' to '{targetPath}': {ex.Message}");
            throw;
        }
    }

    private bool IsKeptLink(string targetPath) => _downloadLinks?.KeepsEditorLink(targetPath) == true;

    private void WhileDownloadLinksAreHeld(Action change)
    {
        if (_downloadLinks is null)
        {
            change();
        }
        else
        {
            _downloadLinks.WhileLinksAreHeld(change);
        }
    }

    private bool PointsIntoSource(string targetPath, string linkTarget)
        => IsUnderRoot(Path.GetFullPath(linkTarget, Path.GetDirectoryName(targetPath)!), _sourceRoot);

    private void RemoveManagedLink(string targetPath)
    {
        try
        {
            if (Directory.Exists(_destinationRoot))
            {
                using var secureRoot = new LinuxSecureDirectoryRoot(_destinationRoot);
                WhileDownloadLinksAreHeld(() =>
                {
                    // A download is going through this link, or went through it and its model may be in use.
                    if (!IsKeptLink(targetPath))
                    {
                        secureRoot.DeleteSymbolicLink(GetRelativeTargetPath(targetPath));
                    }
                });
            }
        }
        catch (Exception ex)
        {
            RunnerLog.Error<LocalModelProjector>(ex, $"Failed removing projected local model '{targetPath}': {ex.Message}");
        }
        finally
        {
            _managedTargets.Remove(targetPath);
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(NormalizePath(left), NormalizePath(right), StringComparison.Ordinal);
    }

    private string GetRelativeTargetPath(string targetPath)
    {
        var relative = Path.GetRelativePath(_destinationRoot, targetPath);
        if (relative == "." ||
            Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Local model projection target '{targetPath}' escapes '{_destinationRoot}'.");
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static bool LinkPointsTo(string linkPath, string linkTarget, string expectedTarget)
    {
        try
        {
            var linkDirectory = Path.GetDirectoryName(linkPath) ?? string.Empty;
            return PathsEqual(Path.Combine(linkDirectory, linkTarget), expectedTarget);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderRoot(string path, string root)
    {
        if (PathsEqual(path, root))
        {
            return true;
        }

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : string.Concat(root, Path.DirectorySeparatorChar);

        return path.StartsWith(rootWithSeparator, StringComparison.Ordinal);
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private readonly record struct LocalProjectionRequest(string SelectionHash, string TargetPath, string Filename);
}

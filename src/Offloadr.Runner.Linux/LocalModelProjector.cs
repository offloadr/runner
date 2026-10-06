using Offloadr.Common.V1;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal sealed class LocalModelProjector
{
    private readonly string _sourceRoot;
    private readonly string _destinationRoot;
    private readonly Dictionary<string, ModelInfo> _modelsBySelectionHash = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ModelInfo>> _modelsByNormalizedFilename = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalProjectionRequest> _desiredProjections = new(StringComparer.Ordinal);
    private readonly HashSet<string> _managedTargets = new(StringComparer.Ordinal);

    public LocalModelProjector(string sourceRoot, string destinationRoot = "/comfyui/models", string runtimeKind = "comfyui")
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
        _ = runtimeKind;
    }

    public void UpdateSnapshot(LocalModelSnapshot snapshot)
    {
        _modelsBySelectionHash.Clear();
        _modelsByNormalizedFilename.Clear();
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
            if (selectionHash is null || _modelsBySelectionHash.ContainsKey(selectionHash))
            {
                continue;
            }

            _modelsBySelectionHash.Add(selectionHash, model);
        }

        ReconcileDesiredProjections();
    }

    public void SetRequestedModels(IEnumerable<ModelDownloadRequest> downloads)
    {
        _desiredProjections.Clear();
        AddProjectionRequests(downloads, reportRemoteFallbacks: false);
        ReconcileDesiredProjections();
    }

    public void AddRequestedModels(IEnumerable<ModelDownloadRequest> downloads)
    {
        AddProjectionRequests(downloads, reportRemoteFallbacks: true);
        ReconcileDesiredProjections();
    }

    public static bool IsLocalProjectionRequest(ModelDownloadRequest? download)
        => !string.IsNullOrWhiteSpace(download?.LocalSelectionHash);

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

    private void ReconcileDesiredProjections()
    {
        var desiredTargets = new HashSet<string>(_desiredProjections.Keys, StringComparer.Ordinal);

        foreach (var projection in _desiredProjections.Values)
        {
            if (!_modelsBySelectionHash.TryGetValue(projection.SelectionHash, out var model))
            {
                throw new InvalidOperationException($"Registered local model '{projection.Filename}' is no longer present in the runner local inventory.");
            }

            var sourcePath = BuildSourcePath(model);
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Registered local model '{projection.Filename}' is no longer readable at '{sourcePath}'.");
            }

            var targetPath = projection.TargetPath;
            if (PathsEqual(sourcePath, targetPath))
            {
                continue;
            }

            EnsureProjectedLink(sourcePath, targetPath);
        }

        foreach (var stale in _managedTargets.ToArray())
        {
            if (desiredTargets.Contains(stale))
            {
                continue;
            }

            RemoveManagedLink(stale);
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

        return sourcePath;
    }

    private void EnsureProjectedLink(string sourcePath, string targetPath)
    {
        try
        {
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);

            if (IsLinkToTarget(targetPath, sourcePath))
            {
                _managedTargets.Add(targetPath);
                return;
            }

            if (IsSymbolicLink(targetPath))
            {
                if (_managedTargets.Contains(targetPath))
                {
                    File.Delete(targetPath);
                }
                else
                {
                    throw new InvalidOperationException($"Projection target '{targetPath}' already exists as an unmanaged symbolic link.");
                }
            }
            else if (File.Exists(targetPath))
            {
                throw new InvalidOperationException($"Projection target '{targetPath}' already exists.");
            }

            File.CreateSymbolicLink(targetPath, sourcePath);
            _managedTargets.Add(targetPath);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<LocalModelProjector>(ex, $"Failed to project local model '{sourcePath}' to '{targetPath}': {ex.Message}");
            throw;
        }
    }

    private void RemoveManagedLink(string targetPath)
    {
        try
        {
            if (IsSymbolicLink(targetPath))
            {
                File.Delete(targetPath);
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

    private static bool IsLinkToTarget(string path, string target)
    {
        if (!File.Exists(path) || !IsSymbolicLink(path))
        {
            return false;
        }

        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            if (resolved is null)
            {
                return false;
            }

            return PathsEqual(resolved.FullName, target);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSymbolicLink(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.LinkTarget is not null)
            {
                return true;
            }

            if (!fileInfo.Exists)
            {
                return false;
            }

            var attrs = fileInfo.Attributes;
            return attrs.HasFlag(FileAttributes.ReparsePoint);
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

using System.Security.Cryptography;
using System.Text;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Stages model downloads whose destination is inside a session home. The session user owns
/// its home and can swap any directory in it for a symlink, so aria2 never writes there by
/// path: it downloads into a per-destination directory beneath the staging directory, which
/// sessions cannot reach, and root moves the completed file into the home through descriptors
/// pinned with <see cref="LinuxSecureDirectoryRoot"/>.
/// </summary>
internal sealed class SessionModelDownloadStaging : IModelDownloadStaging
{
    private readonly string _sessionRoot;
    private readonly string _stagingDirectory;

    public SessionModelDownloadStaging(string sessionRoot, string stagingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        _sessionRoot = Normalize(sessionRoot);
        _stagingDirectory = Normalize(stagingDirectory);
        if (IsSameOrBeneath(_stagingDirectory, _sessionRoot))
        {
            throw new ArgumentException("The staging directory must be outside the session root.", nameof(stagingDirectory));
        }
    }

    public string? GetStagingPath(string destinationPath)
    {
        var destination = Normalize(destinationPath);
        if (!IsStrictlyBeneath(destination, _sessionRoot))
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

        using (var root = new LinuxSecureDirectoryRoot(_sessionRoot))
        {
            root.MoveFileInto(expected, Path.GetRelativePath(_sessionRoot, destination));
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

    /// <summary>Returns true for files aria2 writes beneath the staging directory.</summary>
    public bool IsStagingPath(string? path)
        => !string.IsNullOrWhiteSpace(path) && IsStrictlyBeneath(Normalize(path), _stagingDirectory);

    /// <summary>
    /// Removes everything staged by an earlier run. Session homes do not survive an agent
    /// restart, so nothing can publish it any more.
    /// </summary>
    public void DiscardAll()
    {
        if (!Directory.Exists(_stagingDirectory))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(_stagingDirectory))
        {
            if (Directory.Exists(entry) && new DirectoryInfo(entry).LinkTarget is null)
            {
                DeleteStagingDirectory(entry);
            }
            else
            {
                TryDeleteFile(entry);
            }
        }
    }

    private static void DeleteStagingDirectory(string directory)
    {
        try
        {
            // Recursive deletion removes symbolic links without following them.
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(SessionModelDownloadStaging), $"Could not remove staged download '{directory}': {ex.Message}");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RunnerLog.Warning(nameof(SessionModelDownloadStaging), $"Could not remove staged download '{path}': {ex.Message}");
        }
    }

    private static string StagingKey(string destination)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(destination)))[..32];

    private static bool IsStrictlyBeneath(string path, string root)
        => path.Length > root.Length + 1 &&
           path.StartsWith(root.EndsWith('/') ? root : root + "/", StringComparison.Ordinal);

    private static bool IsSameOrBeneath(string path, string root)
        => string.Equals(path, root, StringComparison.Ordinal) || IsStrictlyBeneath(path, root);

    private static string Normalize(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}

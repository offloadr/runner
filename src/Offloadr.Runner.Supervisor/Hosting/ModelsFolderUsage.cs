namespace Offloadr.Runner.Supervisor.Hosting;

/// <summary>How much a models folder holds: its model files, and downloads the runner has not finished.</summary>
public sealed record ModelsFolderUsage(long Files, long Bytes, long UnfinishedBytes)
{
    /// <summary>Where the runner stages downloads inside the models folder; the agent's ModelLibrary.StagingFolderName.</summary>
    public const string StagingFolderName = ".offloadr-downloads";

    /// <summary>
    /// Adds up <paramref name="folder"/>. Links are not followed (a link to a drive would count that whole
    /// drive), and what cannot be read is skipped. A large folder takes a while; run it off the UI thread.
    /// </summary>
    public static ModelsFolderUsage Measure(string folder, CancellationToken cancellationToken = default)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            ReturnSpecialDirectories = false,
        };
        var staging = Path.Combine(Path.GetFullPath(folder), StagingFolderName) + Path.DirectorySeparatorChar;
        long files = 0, bytes = 0, unfinished = 0;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length;
            try
            {
                length = file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Removed or locked since it was listed.
                continue;
            }

            if (file.FullName.StartsWith(staging, StringComparison.OrdinalIgnoreCase))
            {
                unfinished += length;
            }
            else
            {
                files++;
                bytes += length;
            }
        }

        return new ModelsFolderUsage(files, bytes, unfinished);
    }

    /// <summary>A size as people read it: 512 B, 3.4 MB, 12.5 GB (powers of 1024, as Windows shows them).</summary>
    public static string Format(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : value.ToString(value >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.CurrentCulture) + " " + units[unit];
    }
}

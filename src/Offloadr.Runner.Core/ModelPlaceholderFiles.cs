namespace Offloadr.Runner.Core;

internal static class ModelPlaceholderFiles
{
    public static string? TryCreate(string? fullPath, long expectedLength)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || expectedLength < 0)
        {
            return null;
        }

        try
        {
            var normalized = Path.GetFullPath(fullPath);
            var directory = Path.GetDirectoryName(normalized);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (!File.Exists(normalized))
            {
                using var stream = new FileStream(
                    normalized,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                stream.SetLength(expectedLength);
                return normalized;
            }

            return null;
        }
        catch (Exception ex)
        {
            RunnerLog.Error(nameof(ModelPlaceholderFiles), ex, $"Failed creating placeholder for model path '{fullPath}': {ex.Message}");
            return null;
        }
    }
}

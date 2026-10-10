namespace Offloadr.Runner.Linux;

public static class LinuxSessionIdentity
{
    public static string BuildUserName(string sessionId)
    {
        var sanitized = new string((sessionId ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (string.IsNullOrEmpty(sanitized))
        {
            sanitized = "job";
        }

        if (sanitized.Length > 12)
        {
            sanitized = sanitized[..12];
        }

        return $"sess_{sanitized}";
    }
}

using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Logs for a sealed session's services. The console reaches the host's disk, so only values
/// known not to name session files (ids, counts, sizes, limits) are written; every other value,
/// such as a path, filename or root, is replaced. An exception is logged by its type only:
/// filesystem errors carry the path in their message.
/// </summary>
internal sealed partial class SealedSessionLogger(ILogger inner) : ILogger
{
    private const string Redacted = "[sealed]";

    private static readonly HashSet<string> SafeValueNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "SessionId",
        "EditorSid",
        "Type",
        "Event",
        "Exists",
        "Count",
        "Bytes",
        "Size",
        "Limit",
        "Attempt",
        "Dropped",
        "Description",
        "TimeoutSeconds",
        "MaxUploadBytes",
        "MaxWorkspaceFileBytes",
        "MaxArchiveBytes",
        "MaxExpandedBytes",
        "MaxArchiveEntries",
    };

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => inner.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var message = state is IReadOnlyList<KeyValuePair<string, object?>> values
            ? Render(values)
            : Redacted;
        if (exception is not null)
        {
            message += $" error={exception.GetType().Name}";
        }

        inner.Log(logLevel, eventId, message, null, static (rendered, _) => rendered);
    }

    internal static string Render(IReadOnlyList<KeyValuePair<string, object?>> values)
    {
        var template = values.FirstOrDefault(static pair => pair.Key == "{OriginalFormat}").Value as string;
        if (template is null)
        {
            return Redacted;
        }

        var byName = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            byName[key] = value;
        }

        return Placeholder().Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            if (!SafeValueNames.Contains(name))
            {
                return Redacted;
            }

            return byName.TryGetValue(name, out var value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty : match.Value;
        });
    }

    [GeneratedRegex(@"\{(?<name>[^{}:,]+)(?:[,:][^{}]*)?\}")]
    private static partial Regex Placeholder();
}

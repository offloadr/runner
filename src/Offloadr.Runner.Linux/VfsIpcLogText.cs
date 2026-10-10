using System.Globalization;
using System.Text;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Makes client-supplied text (paths, exception messages that embed them) safe for one log
/// line: control and formatting characters are escaped and the result is bounded.
/// </summary>
internal static class VfsIpcLogText
{
    public const int DefaultMaxLength = 256;

    public static string Sanitize(string? value, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength) + 16);
        var index = 0;
        for (; index < value.Length && builder.Length < maxLength; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                builder.Append(@"\\");
            }
            else if (char.IsControl(character) ||
                     char.IsSurrogate(character) ||
                     CharUnicodeInfo.GetUnicodeCategory(character) is
                         UnicodeCategory.Format or
                         UnicodeCategory.LineSeparator or
                         UnicodeCategory.ParagraphSeparator)
            {
                // Escapes newlines, terminal escapes, bidi overrides and unpaired halves alike.
                builder.Append(@"\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        if (index < value.Length)
        {
            builder.Append(CultureInfo.InvariantCulture, $"...(+{value.Length - index} chars)");
        }

        return builder.ToString();
    }
}

/// <summary>
/// Allows a bounded number of log lines per window and reports how many were dropped, so
/// clients that make requests fail on purpose cannot flood the agent's log.
/// </summary>
internal sealed class LogRateLimiter
{
    private readonly int _maxPerWindow;
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private DateTime _windowStart = DateTime.MinValue;
    private int _allowed;
    private int _suppressed;

    public LogRateLimiter(int maxPerWindow, TimeSpan window, Func<DateTime>? utcNow = null)
    {
        _maxPerWindow = maxPerWindow > 0 ? maxPerWindow : throw new ArgumentOutOfRangeException(nameof(maxPerWindow));
        _window = window > TimeSpan.Zero ? window : throw new ArgumentOutOfRangeException(nameof(window));
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
    }

    /// <summary>
    /// True when the caller may log now; <paramref name="suppressedBefore"/> is the number of
    /// lines dropped in the previous window, to be mentioned once.
    /// </summary>
    public bool TryAcquire(out int suppressedBefore)
    {
        lock (_gate)
        {
            suppressedBefore = 0;
            var now = _utcNow();
            if (now - _windowStart >= _window)
            {
                suppressedBefore = _suppressed;
                _windowStart = now;
                _allowed = 0;
                _suppressed = 0;
            }

            if (_allowed < _maxPerWindow)
            {
                _allowed++;
                return true;
            }

            _suppressed++;
            return false;
        }
    }
}

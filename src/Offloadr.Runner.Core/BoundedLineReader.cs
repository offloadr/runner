using System.Text;

namespace Offloadr.Runner.Core;

/// <summary>
/// Reads text output line by line like <c>Process.OutputDataReceived</c>, but never buffers more
/// than <c>maxLineChars</c> of one line: an overlong line is delivered in pieces, and every piece
/// after the first starts with <see cref="ContinuationPrefix"/>. Output from untrusted processes
/// therefore cannot exhaust memory by never writing a newline.
/// </summary>
internal static class BoundedLineReader
{
    public const string ContinuationPrefix = "[continued] ";

    /// <summary>Leaves room for the prefix so an ASCII piece stays within the log relay's message bound.</summary>
    public const int DefaultMaxLineChars = LogRelayBounds.MaxMessageBytes - 64;

    /// <summary>
    /// Delivers each line of <paramref name="reader"/> to <paramref name="onLine"/> until end of
    /// stream. '\n', '\r' and "\r\n" end a line, as with <see cref="TextReader.ReadLine"/>.
    /// Cancellation is passed to the original read. The caller must still join the returned
    /// task: a reader that ignores cancellation can keep it pending until that read ends.
    /// Stream closure and cancellation discard an unfinished line and do not fault the pump.
    /// </summary>
    public static async Task PumpAsync(
        TextReader reader,
        Action<string> onLine,
        int maxLineChars = DefaultMaxLineChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLineChars, 2);

        var buffer = new char[4096];
        var line = new StringBuilder();
        var continued = false;
        var previousWasCarriageReturn = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                return;
            }

            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                var c = buffer[i];
                if (c == '\n' && previousWasCarriageReturn)
                {
                    previousWasCarriageReturn = false;
                    continue;
                }

                previousWasCarriageReturn = c == '\r';
                if (c is '\n' or '\r')
                {
                    if (!continued || line.Length > 0)
                    {
                        Deliver(line, continued, onLine);
                    }

                    continued = false;
                    continue;
                }

                line.Append(c);
                if (line.Length >= maxLineChars && !char.IsHighSurrogate(c))
                {
                    Deliver(line, continued, onLine);
                    continued = true;
                }
            }
        }

        if (!cancellationToken.IsCancellationRequested && line.Length > 0)
        {
            Deliver(line, continued, onLine);
        }
    }

    private static void Deliver(StringBuilder line, bool continued, Action<string> onLine)
    {
        var text = continued ? string.Concat(ContinuationPrefix, line.ToString()) : line.ToString();
        line.Clear();
        try
        {
            onLine(text);
        }
        catch
        {
            // A failing consumer must not stop the pump: an unread pipe would block the writer.
        }
    }
}

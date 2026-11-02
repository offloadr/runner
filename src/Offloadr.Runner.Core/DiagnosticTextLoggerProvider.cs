using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Core;

/// <summary>
/// Console for a runner on its owner's machine (<see cref="ConsoleLogMode.Full"/>): the formatted
/// message and any exception with its stack, after the timestamp, severity and component labels of
/// <see cref="DiagnosticMetadataLoggerProvider"/>. Scopes are not rendered.
/// </summary>
internal sealed class DiagnosticTextLoggerProvider(Action<string> write, TimeProvider? clock = null) : ILoggerProvider
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ILogger CreateLogger(string categoryName) => new TextLogger(this, DiagnosticMetadataLoggerProvider.Component(categoryName));

    public void Dispose() { }

    private void Write(string component, LogLevel level, EventId eventId, string message, Exception? error)
    {
        var severity = DiagnosticMetadataLoggerProvider.Severity(level);
        if (severity is null) return;

        var timestamp = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        var eventName = string.IsNullOrWhiteSpace(eventId.Name) ? string.Empty : eventId.Name + " ";
        var line = $"{timestamp} {severity} {component}: {eventName}{message}";
        if (error is not null) line = $"{line}{Environment.NewLine}{error}";
        try
        {
            write(line);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Optional diagnostics must not interrupt the owner of the operation.
        }
    }

    private sealed class TextLogger(DiagnosticTextLoggerProvider provider, string component) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is >= LogLevel.Information and <= LogLevel.Critical;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            provider.Write(component, level, eventId, formatter(state, error), error);
        }
    }
}

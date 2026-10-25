using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Offloadr.Runner.Core;

/// <summary>
/// Platform logs are outside the account-protected presentation path. Project only
/// fixed operational labels here, without rendering state, scopes or exception text.
/// </summary>
internal sealed class DiagnosticMetadataLoggerProvider(Action<string> write, TimeProvider? clock = null) : ILoggerProvider
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ILogger CreateLogger(string categoryName) => new MetadataLogger(this, Component(categoryName));

    public void Dispose() { }

    private void Write(string component, LogLevel level, EventId eventId, Exception? error)
    {
        var severity = level switch
        {
            LogLevel.Information => "information",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => null
        };
        if (severity is null) return;

        var errorClass = error switch
        {
            null => "none",
            OperationCanceledException => "cancelled",
            TimeoutException => "timeout",
            UnauthorizedAccessException or System.Security.SecurityException => "access",
            IOException => "io",
            ArgumentException => "argument",
            InvalidOperationException => "state",
            HttpRequestException => "http",
            Grpc.Core.RpcException => "rpc",
            _ => "unexpected"
        };
        var timestamp = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        try
        {
            var startup = component == "agent" && eventId.Id == RunnerDiagnosticEvents.StartupValidationFailed.Id
                ? " event=startup-validation-failed Runner startup validation failed."
                : string.Empty;
            write($"{timestamp} severity={severity} component={component} errorClass={errorClass}{startup}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            // Optional diagnostics must not interrupt the owner of the operation.
        }
    }

    private static string Component(string category)
    {
        if (category.StartsWith("session:", StringComparison.OrdinalIgnoreCase)) return "editor";
        return category switch
        {
            "RunnerAgent" => "agent",
            "Offloadr.Runner.Desktop.Startup" => "desktop-startup",
            "Offloadr.Runner.Linux.SessionProcessManager" => "session-process",
            "Offloadr.Runner.Linux.WorkspaceMirrorService" => "workspace",
            "Offloadr.Runner.Linux.ArtifactUploadService" => "artifacts",
            "Offloadr.Runner.Linux.DemandAwareModelHydrationCoordinator" => "model-transfer",
            _ when category.StartsWith("Offloadr.Runner.Supervisor.Settings.", StringComparison.Ordinal) => "settings",
            _ when category.StartsWith("Offloadr.Runner.Supervisor.Hosting.", StringComparison.Ordinal) => "desktop-host",
            _ when category.StartsWith("Offloadr.Runner.Supervisor.", StringComparison.Ordinal) => "supervisor",
            _ when category.StartsWith("Offloadr.Runner.Desktop.", StringComparison.Ordinal) => "desktop",
            _ => "other"
        };
    }

    private sealed class MetadataLogger(DiagnosticMetadataLoggerProvider provider, string component) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level is >= LogLevel.Information and <= LogLevel.Critical;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => provider.Write(component, level, eventId, error);
    }
}

internal static class RunnerDiagnosticEvents
{
    public static readonly EventId StartupValidationFailed = new(9001);
}

using System.Runtime.InteropServices;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Offloadr.Runner.Desktop;

/// <summary>MAUI does not run hosted services, so the app starts them in order and stops them in reverse.</summary>
public sealed class DesktopRuntime(IEnumerable<IHostedService> services, ILogger<DesktopRuntime> logger)
{
    private readonly List<IHostedService> _started = [];

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            foreach (var service in services)
            {
                await service.StartAsync(cancellationToken);
                _started.Add(service);
            }
        }
        catch
        {
            await StopAsync();
            throw;
        }
    }

    /// <summary>Stops everything, including the runner container. The supervisor waits for the runner to shut down cleanly.</summary>
    public async Task StopAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        foreach (var service in Enumerable.Reverse(_started))
        {
            try
            {
                await service.StopAsync(timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "A background service did not stop cleanly.");
            }
        }

        _started.Clear();
    }
}

/// <summary>MAUI's default host environment lacks a content root, which Data Protection needs.</summary>
internal sealed class DesktopHostEnvironment : IHostEnvironment
{
    public string ApplicationName { get; set; } = "OffloadrRunner";

    public string EnvironmentName { get; set; } = Environments.Production;

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

/// <summary>
/// Two copies of the app would fight over the same runner container, so the second one does not
/// start: it asks the first one to show its window (often hidden in the notification area) instead.
/// A held file handle, not a PID file, owns the lock, so a crash never leaves it stale.
/// </summary>
public sealed class SingleInstanceLock(SupervisorPaths paths) : IHostedService, IDisposable
{
    private FileStream? _lock;
    private EventWaitHandle? _openSignal;
    private RegisteredWaitHandle? _openWait;

    /// <summary>Raised on a background thread when another launch of the app asks this one to show its window.</summary>
    public event Action? OpenRequested;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(paths.Data);
        try
        {
            _lock = new FileStream(Path.Combine(paths.Data, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new AlreadyOpenException(SignalTheOpenCopy(), ex);
        }

        _openSignal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName());
        _openWait = ThreadPool.RegisterWaitForSingleObject(_openSignal, (_, _) => OpenRequested?.Invoke(), null, Timeout.Infinite, executeOnlyOnce: false);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _openWait?.Unregister(null);
        _openWait = null;
        _openSignal?.Dispose();
        _openSignal = null;
        _lock?.Dispose();
        _lock = null;
    }

    private bool SignalTheOpenCopy()
    {
        try
        {
            using var signal = EventWaitHandle.OpenExisting(SignalName());

            // The open copy runs in the background, so Windows would not let it take the foreground;
            // this launch, just started by the person, may pass that right on.
            AllowSetForegroundWindow(AsfwAny);
            return signal.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    /// <summary>One per data folder and Windows session, like the lock itself.</summary>
    private string SignalName()
        => "Local\\OffloadrRunner-open-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.Data.ToUpperInvariant())))[..16];
}

/// <summary>Another copy of the app already runs the runner.</summary>
public sealed class AlreadyOpenException(bool broughtUp, Exception inner) : Exception(
    broughtUp
        ? "Offloadr Runner is already open; its window has been brought up."
        : "Offloadr Runner is already open. Open it from its icon in the notification area.",
    inner)
{
    /// <summary>True when the open copy was asked to show its window, so this one can simply quit.</summary>
    public bool BroughtUp { get; } = broughtUp;
}

/// <summary>Writes warnings and runner activity to a daily file under the app's data folder.</summary>
internal sealed class FileLoggerProvider(string directory) : ILoggerProvider
{
    private readonly object _gate = new();

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = $"{now:O} {level} {category}: {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}";
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, $"runner-{now:yyyy-MM-dd}.log"), line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Logging must never take the app down, including when the log folder becomes unwritable.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel >= LogLevel.Warning ||
               (logLevel >= LogLevel.Information && category.StartsWith("Offloadr.", StringComparison.Ordinal));

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Offloadr.Runner.Core;

internal static class RunnerLog
{
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;
    private const string DefaultCategory = "RunnerAgent";

    public static void Configure(ILoggerFactory? loggerFactory)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public static void Info<TCategory>(string message)
    {
        _loggerFactory.CreateLogger<TCategory>().LogInformation("{Message}", message);
    }

    public static void Info(string message)
    {
        _loggerFactory.CreateLogger(DefaultCategory).LogInformation("{Message}", message);
    }

    public static void Info(string category, string message)
    {
        _loggerFactory.CreateLogger(string.IsNullOrWhiteSpace(category) ? DefaultCategory : category).LogInformation("{Message}", message);
    }

    public static void Event<TCategory>(
        string eventName,
        string messageTemplate,
        params object?[] propertyValues)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            throw new ArgumentException("Event name is required.", nameof(eventName));
        }

        _loggerFactory
            .CreateLogger<TCategory>()
            .LogInformation(
                new EventId(0, eventName.Trim()),
                messageTemplate,
                propertyValues);
    }

    public static void Debug<TCategory>(string message)
    {
        _loggerFactory.CreateLogger<TCategory>().LogDebug("{Message}", message);
    }

    public static void Debug(string message)
    {
        _loggerFactory.CreateLogger(DefaultCategory).LogDebug("{Message}", message);
    }

    public static void Debug(string category, string message)
    {
        _loggerFactory.CreateLogger(string.IsNullOrWhiteSpace(category) ? DefaultCategory : category).LogDebug("{Message}", message);
    }

    public static void Warning<TCategory>(string message)
    {
        _loggerFactory.CreateLogger<TCategory>().LogWarning("{Message}", message);
    }

    public static void Warning(string message)
    {
        _loggerFactory.CreateLogger(DefaultCategory).LogWarning("{Message}", message);
    }

    public static void Warning(string category, string message)
    {
        _loggerFactory.CreateLogger(string.IsNullOrWhiteSpace(category) ? DefaultCategory : category).LogWarning("{Message}", message);
    }

    public static void Error<TCategory>(string message)
    {
        _loggerFactory.CreateLogger<TCategory>().LogError("{Message}", message);
    }

    public static void Error(string message)
    {
        _loggerFactory.CreateLogger(DefaultCategory).LogError("{Message}", message);
    }

    public static void Error(string category, string message)
    {
        _loggerFactory.CreateLogger(string.IsNullOrWhiteSpace(category) ? DefaultCategory : category).LogError("{Message}", message);
    }

    public static void Error<TCategory>(Exception ex, string message)
    {
        _loggerFactory.CreateLogger<TCategory>().LogError(ex, "{Message}", message);
    }

    public static void Error(Exception ex, string message)
    {
        _loggerFactory.CreateLogger(DefaultCategory).LogError(ex, "{Message}", message);
    }

    public static void Error(string category, Exception ex, string message)
    {
        _loggerFactory.CreateLogger(string.IsNullOrWhiteSpace(category) ? DefaultCategory : category).LogError(ex, "{Message}", message);
    }
}

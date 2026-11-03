using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;

namespace Offloadr.Runner.Agent;

internal static class AgentLogging
{
    public static ILoggerFactory CreateFactory(RunnerLogRelay runnerLogRelay, ConsoleLogMode consoleMode)
        => LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(consoleMode == ConsoleLogMode.Full
                ? new DiagnosticTextLoggerProvider(Console.WriteLine)
                : new DiagnosticMetadataLoggerProvider(Console.WriteLine));
            builder.AddProvider(new RunnerLogForwardingProvider(runnerLogRelay));
            builder.SetMinimumLevel(LogLevel.Information);
        });
}

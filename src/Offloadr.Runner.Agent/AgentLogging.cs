using Microsoft.Extensions.Logging;
using Offloadr.Runner.Core;

namespace Offloadr.Runner.Agent;

internal static class AgentLogging
{
    public static ILoggerFactory CreateFactory(RunnerLogRelay runnerLogRelay)
        => LoggerFactory.Create(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(new DiagnosticMetadataLoggerProvider(Console.WriteLine));
            builder.AddProvider(new RunnerLogForwardingProvider(runnerLogRelay));
            builder.SetMinimumLevel(LogLevel.Information);
        });
}

using System.Runtime.InteropServices;

namespace Offloadr.Runner.Agent;

internal sealed class RunnerAgentShutdownSignals : IDisposable
{
    private readonly ConsoleCancelEventHandler _cancelKeyPress;
    private readonly PosixSignalRegistration _sigterm;

    public RunnerAgentShutdownSignals(RunnerAgentLifetime lifetime)
    {
        _cancelKeyPress = (_, context) =>
        {
            context.Cancel = true;
            _ = lifetime.RequestShutdownAsync();
        };
        _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            _ = lifetime.RequestShutdownAsync();
        });
        Console.CancelKeyPress += _cancelKeyPress;
    }

    public void Dispose()
    {
        Console.CancelKeyPress -= _cancelKeyPress;
        _sigterm.Dispose();
    }
}

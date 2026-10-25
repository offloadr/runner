using Offloadr.Runner.V1;

namespace Offloadr.Runner.Tests;

internal sealed class TestRuntimeActivitySink : IRuntimeActivitySink
{
    public static IRuntimeActivitySink Accepted { get; } = new TestRuntimeActivitySink();
    public Task<RuntimeActivityOutcome> ReportAsync(RuntimeActivityObservation observation, CancellationToken cancellationToken)
        => Task.FromResult(RuntimeActivityOutcome.Applied);
}

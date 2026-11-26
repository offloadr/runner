using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// What each command stream this process opens says about the streams before it: its sequence, which rises
/// by one with every open (from 1), and the highest Start order received on the earlier ones: admitted, or
/// refused with its order settled. Admission decides before the next command is read, so by the time a new
/// stream opens every Start read is counted or not, and each one counted settles (an admitted one, adopted
/// or not ready). Used by the one loop that reads the command stream, for the life of the process.
/// </summary>
internal sealed class CommandStreamOpens
{
    private ulong _sequence;
    private ulong _highestRead;

    /// <summary>The request that opens the next command stream.</summary>
    public CommandStreamRequest Next(string runnerId, string version, string instanceId) => new()
    {
        RunnerId = runnerId,
        Version = version,
        InstanceId = instanceId,
        ProtocolVersion = RunnerProtocolVersion.Current,
        ReceivedRuntimeOrder = _highestRead,
        CommandStreamSequence = ++_sequence,
    };

    /// <summary>
    /// Notes the order of a Start admission admitted, or refused with its order settled. One it could not order,
    /// or that reuses the newest Start's runtime, is never passed here: it settles nothing, and would stand
    /// above every valid Start for the life of the process.
    /// </summary>
    public void Received(ulong order)
    {
        if (order > _highestRead)
        {
            _highestRead = order;
        }
    }
}

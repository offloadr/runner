using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

internal static class ResourceDetection
{
    private static readonly ResourceDiscoveryService Service = new();

    public static RunnerResources BuildInitialResources()
    {
        return Service.BuildInitialResources();
    }

    public static ulong GetTotalRamBytes()
    {
        return Service.GetTotalRamBytes();
    }
}

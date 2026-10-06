using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Tests;

internal static class LinuxTestPrerequisites
{
    private static bool NativeTestsRequired
        => string.Equals(
            Environment.GetEnvironmentVariable("RUNNER_NATIVE_TESTS_REQUIRED"),
            "1",
            StringComparison.Ordinal);

    public static void RequireLinux()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            FailOrIgnore("Linux-only test.");
        }
    }

    public static void RequireUnixDomainSockets()
    {
        try
        {
            using var _ = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        }
        catch (Exception ex)
        {
            FailOrIgnore($"Unix domain sockets unavailable: {ex.Message}");
        }
    }

    public static void RequireCommand(string fullPath)
    {
        if (!File.Exists(fullPath))
        {
            FailOrIgnore($"Required command not available: {fullPath}");
        }
    }

    private static void FailOrIgnore(string message)
    {
        if (NativeTestsRequired)
        {
            Assert.Fail(message);
        }

        Assert.Ignore(message);
    }
}

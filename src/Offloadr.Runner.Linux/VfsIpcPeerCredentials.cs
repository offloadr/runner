using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Kernel-reported identity of the process on the other end of a Unix socket
/// (SO_PEERCRED). Unlike anything in the request payload, the client cannot forge it.
/// </summary>
public readonly record struct VfsIpcPeerCredentials(int ProcessId, uint UserId, uint GroupId)
{
    public const uint RootUserId = 0;

    public bool IsRoot => UserId == RootUserId;

    public override string ToString() => $"pid={ProcessId} uid={UserId} gid={GroupId}";

    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int UcredLength = 12;

    public static bool TryRead(Socket socket, out VfsIpcPeerCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(socket);
        credentials = default;
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[UcredLength];
        try
        {
            if (socket.GetRawSocketOption(SolSocket, SoPeerCred, buffer) < UcredLength)
            {
                return false;
            }
        }
        catch (SocketException)
        {
            return false;
        }

        // struct ucred { pid_t pid; uid_t uid; gid_t gid; } in host byte order.
        credentials = new VfsIpcPeerCredentials(
            MemoryMarshal.Read<int>(buffer[..4]),
            MemoryMarshal.Read<uint>(buffer.Slice(4, 4)),
            MemoryMarshal.Read<uint>(buffer.Slice(8, 4)));
        return true;
    }

    /// <summary>The effective uid of this process, or null where it is not available.</summary>
    public static uint? CurrentEffectiveUserId
    {
        get
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return null;
            }

            try
            {
                return geteuid();
            }
            catch (EntryPointNotFoundException)
            {
                return null;
            }
            catch (DllNotFoundException)
            {
                return null;
            }
        }
    }

    [DllImport("libc")]
    private static extern uint geteuid();
}

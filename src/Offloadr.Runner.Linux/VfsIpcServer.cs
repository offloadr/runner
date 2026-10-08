using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Offloadr.Runner.Linux;

public sealed class VfsIpcServer : IAsyncDisposable
{
    private const int DefaultMaxConcurrentClients = 64;
    private static readonly TimeSpan DefaultRequestReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DefaultOpenAcknowledgementTimeout = TimeSpan.FromSeconds(10);

    private readonly string _socketPath;
    private readonly Func<VfsIpcRequest, VfsIpcPeerCredentials, CancellationToken, Task<VfsIpcResponse>> _requestHandler;
    private readonly Func<VfsIpcPeerCredentials, bool> _authorizePeer;
    private readonly bool _logEnabled;
    private readonly TimeSpan _requestReadTimeout;
    private readonly TimeSpan _openAcknowledgementTimeout;
    private readonly SemaphoreSlim _clientSlots;
    private readonly object _clientTasksGate = new();
    private readonly HashSet<Task> _clientTasks = [];
    private readonly object _provisionalLeasesGate = new();
    private readonly Dictionary<ulong, ProvisionalOpenLease> _provisionalLeases = [];
    private readonly HashSet<Task> _provisionalExpirationTasks = [];
    private readonly CancellationTokenSource _shutdown = new();

    private Socket? _listener;
    private Task? _acceptLoop;

    public VfsIpcServer(
        string socketPath,
        Func<VfsIpcRequest, CancellationToken, Task<VfsIpcResponse>> requestHandler,
        bool logEnabled = false,
        TimeSpan? requestReadTimeout = null,
        int maxConcurrentClients = DefaultMaxConcurrentClients,
        TimeSpan? openAcknowledgementTimeout = null,
        Func<VfsIpcPeerCredentials, bool>? authorizePeer = null)
        : this(
            socketPath,
            IgnorePeer(requestHandler),
            logEnabled,
            requestReadTimeout,
            maxConcurrentClients,
            openAcknowledgementTimeout,
            authorizePeer)
    {
    }

    /// <param name="authorizePeer">
    /// Decides, from the kernel-reported peer credentials, whether a connection may issue
    /// requests. When omitted only root and this process's own uid are accepted.
    /// </param>
    public VfsIpcServer(
        string socketPath,
        Func<VfsIpcRequest, VfsIpcPeerCredentials, CancellationToken, Task<VfsIpcResponse>> requestHandler,
        bool logEnabled = false,
        TimeSpan? requestReadTimeout = null,
        int maxConcurrentClients = DefaultMaxConcurrentClients,
        TimeSpan? openAcknowledgementTimeout = null,
        Func<VfsIpcPeerCredentials, bool>? authorizePeer = null)
    {
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            throw new ArgumentException("Socket path is required.", nameof(socketPath));
        }

        _socketPath = socketPath;
        _requestHandler = requestHandler ?? throw new ArgumentNullException(nameof(requestHandler));
        _authorizePeer = authorizePeer ?? IsRootOrCurrentUser;
        _logEnabled = logEnabled;
        _requestReadTimeout = requestReadTimeout ?? DefaultRequestReadTimeout;
        if (_requestReadTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestReadTimeout));
        }

        _openAcknowledgementTimeout =
            openAcknowledgementTimeout ?? DefaultOpenAcknowledgementTimeout;
        if (_openAcknowledgementTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(openAcknowledgementTimeout));
        }

        if (maxConcurrentClients <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrentClients));
        }

        _clientSlots = new SemaphoreSlim(maxConcurrentClients, maxConcurrentClients);
    }

    public string SocketPath => _socketPath;

    private static Func<VfsIpcRequest, VfsIpcPeerCredentials, CancellationToken, Task<VfsIpcResponse>> IgnorePeer(
        Func<VfsIpcRequest, CancellationToken, Task<VfsIpcResponse>> requestHandler)
    {
        ArgumentNullException.ThrowIfNull(requestHandler);
        return (request, _, cancellationToken) => requestHandler(request, cancellationToken);
    }

    /// <summary>Credentials passed to the handler for releases the server itself initiates.</summary>
    private static VfsIpcPeerCredentials ServerPeer
        => new(Environment.ProcessId, VfsIpcPeerCredentials.CurrentEffectiveUserId ?? VfsIpcPeerCredentials.RootUserId, 0);

    private static bool IsRootOrCurrentUser(VfsIpcPeerCredentials peer)
        => peer.IsRoot || peer.UserId == VfsIpcPeerCredentials.CurrentEffectiveUserId;

    public void Start()
    {
        if (_listener != null)
        {
            throw new InvalidOperationException("IPC server already started.");
        }

        var directory = Path.GetDirectoryName(_socketPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            EnsureSocketPathAncestorsAccessible(directory);
        }

        if (File.Exists(_socketPath))
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch
            {
                // Best effort cleanup of stale socket file.
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(_socketPath));
        socket.Listen(backlog: 16);
        _listener = socket;

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Session users must be able to connect; who may issue requests is decided
            // per connection from the kernel-reported peer credentials.
            try
            {
                File.SetUnixFileMode(
                    _socketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
            }
            catch
            {
                // Best effort permission hardening.
            }
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
    }

    private static void EnsureSocketPathAncestorsAccessible(string directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        string? current;
        try
        {
            current = Path.GetFullPath(directory);
        }
        catch
        {
            current = directory;
        }

        while (!string.IsNullOrWhiteSpace(current))
        {
            EnsureSocketDirectorySearchable(current);

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }
    }

    private static void EnsureSocketDirectorySearchable(string directory)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return;
        }

        try
        {
            var mode = File.GetUnixFileMode(directory);
            File.SetUnixFileMode(
                directory,
                mode |
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupExecute |
                UnixFileMode.OtherExecute);
        }
        catch
        {
            // Best effort: startup can still fail loudly at bind/connect time if the path is unusable.
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        if (_listener == null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? client = null;
            var slotAcquired = false;
            try
            {
                await _clientSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                slotAcquired = true;
                client = await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                var acceptedClient = client;
                client = null;
                var clientTask = Task.Run(
                    () => HandleClientAsync(acceptedClient, cancellationToken),
                    CancellationToken.None);
                lock (_clientTasksGate)
                {
                    _clientTasks.Add(clientTask);
                }

                _ = clientTask.ContinueWith(
                    completedTask =>
                    {
                        lock (_clientTasksGate)
                        {
                            _clientTasks.Remove(completedTask);
                        }

                        _clientSlots.Release();
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                slotAcquired = false;
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                if (slotAcquired)
                {
                    _clientSlots.Release();
                }

                break;
            }
            catch (Exception ex)
            {
                RunnerLog.Error<VfsIpcServer>(ex, $"[vfs-ipc] Accept failed: {ex.Message}");
                client?.Dispose();
                if (slotAcquired)
                {
                    _clientSlots.Release();
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;

                if (!TryAuthorizePeer(client, out var peer))
                {
                    await VfsIpcProtocol
                        .WriteResponseAsync(client, VfsIpcResponse.Error(VfsIpcStatus.IoError), cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                using var requestReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestReadCancellation.CancelAfter(_requestReadTimeout);
                var parsed = await VfsIpcProtocol
                    .TryReadRequestAsync(client, requestReadCancellation.Token)
                    .ConfigureAwait(false);
                if (!parsed.Success)
                {
                    await VfsIpcProtocol
                        .WriteResponseAsync(client, VfsIpcResponse.Error(parsed.ErrorStatus), cancellationToken)
                        .ConfigureAwait(false);
                    return;
                }

                try
                {
                    if (_logEnabled)
                    {
                        RunnerLog.Info<VfsIpcServer>(
                            $"[vfs-ipc] operation={parsed.Request.Operation} path='{parsed.Request.Path}'");
                    }

                    var response = await DispatchRequestAsync(parsed.Request, peer, cancellationToken)
                        .ConfigureAwait(false);
                    if (IsProvisionalOpenResponse(parsed.Request, response))
                    {
                        RegisterProvisionalOpenLease(response.LeaseId, peer.UserId);
                    }

                    try
                    {
                        await VfsIpcProtocol.WriteResponseAsync(client, response, cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        await ReleaseUndeliveredOpenLeaseAsync(parsed.Request, response).ConfigureAwait(false);
                        throw;
                    }

                    if (_logEnabled && response.Status == VfsIpcStatus.Success)
                    {
                        RunnerLog.Info<VfsIpcServer>(
                            $"[vfs-ipc] ready operation={parsed.Request.Operation} path='{parsed.Request.Path}' disposition={response.OpenDisposition}");
                    }
                }
                catch (Exception ex)
                {
                    RunnerLog.Error<VfsIpcServer>(
                        ex,
                        $"[vfs-ipc] Operation {parsed.Request.Operation} failed for '{parsed.Request.Path}': {ex.Message}");
                    await VfsIpcProtocol
                        .WriteResponseAsync(
                            client,
                            VfsIpcResponse.Error(
                                ex is ModelHydrationIOException ? VfsIpcStatus.IoError : VfsIpcStatus.EnsureFailed,
                                ex is ModelHydrationIOException hydrationException
                                    ? hydrationException.TransferEpoch
                                    : 0),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await VfsIpcProtocol
                        .WriteResponseAsync(client, VfsIpcResponse.Error(VfsIpcStatus.Cancelled))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Ignore post-cancel response failures.
                }
            }
            catch (Exception ex)
            {
                RunnerLog.Error<VfsIpcServer>(ex, $"[vfs-ipc] Client handling error: {ex.Message}");
                try
                {
                    await VfsIpcProtocol
                        .WriteResponseAsync(client, VfsIpcResponse.Error(VfsIpcStatus.ServerError))
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Ignore secondary failures during error response.
                }
            }
        }
    }

    private bool TryAuthorizePeer(Socket client, out VfsIpcPeerCredentials peer)
    {
        if (!VfsIpcPeerCredentials.TryRead(client, out peer))
        {
            RunnerLog.Warning<VfsIpcServer>("[vfs-ipc] Rejected a client whose peer credentials could not be read.");
            return false;
        }

        bool authorized;
        try
        {
            authorized = _authorizePeer(peer);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<VfsIpcServer>(ex, $"[vfs-ipc] Peer authorization failed: {ex.Message}");
            authorized = false;
        }

        if (!authorized)
        {
            RunnerLog.Warning<VfsIpcServer>($"[vfs-ipc] Rejected unauthorized client {peer}.");
        }

        return authorized;
    }

    private async Task<VfsIpcResponse> DispatchRequestAsync(
        VfsIpcRequest request,
        VfsIpcPeerCredentials peer,
        CancellationToken cancellationToken)
    {
        if (request.Operation == VfsIpcOperation.AcknowledgeOpen)
        {
            return await AcknowledgeOpenLeaseAsync(request.LeaseId, peer.UserId).ConfigureAwait(false);
        }

        if ((request.Operation is VfsIpcOperation.EnsureRange or VfsIpcOperation.EnsureComplete) &&
            IsProvisionalOpenLease(request.LeaseId))
        {
            return VfsIpcResponse.Error(VfsIpcStatus.IoError, request.TransferEpoch);
        }

        if (request.Operation == VfsIpcOperation.Release)
        {
            await CancelProvisionalOpenLeaseAsync(request.LeaseId, peer.UserId).ConfigureAwait(false);
        }

        return await _requestHandler(request, peer, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsProvisionalOpenResponse(
        VfsIpcRequest request,
        VfsIpcResponse response)
        => request.Operation == VfsIpcOperation.Open &&
           response.Status == VfsIpcStatus.Success &&
           response.OpenDisposition == VfsIpcOpenDisposition.RangeManaged &&
           response.LeaseId != 0;

    private bool IsProvisionalOpenLease(ulong leaseId)
    {
        lock (_provisionalLeasesGate)
        {
            return _provisionalLeases.ContainsKey(leaseId);
        }
    }

    private void RegisterProvisionalOpenLease(ulong leaseId, uint ownerUserId)
    {
        var provisional = new ProvisionalOpenLease(
            leaseId,
            ownerUserId,
            CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        lock (_provisionalLeasesGate)
        {
            if (_provisionalLeases.ContainsKey(leaseId))
            {
                provisional.ExpirationCancellation.Dispose();
                throw new InvalidOperationException(
                    $"VFS Open returned duplicate provisional lease '{leaseId}'.");
            }

            _provisionalLeases.Add(leaseId, provisional);
            provisional.ExpirationTask = ExpireProvisionalOpenLeaseAsync(leaseId, provisional);
            _provisionalExpirationTasks.Add(provisional.ExpirationTask);
            _ = provisional.ExpirationTask.ContinueWith(
                completedTask =>
                {
                    lock (_provisionalLeasesGate)
                    {
                        _provisionalExpirationTasks.Remove(completedTask);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task ExpireProvisionalOpenLeaseAsync(
        ulong leaseId,
        ProvisionalOpenLease provisional)
    {
        try
        {
            await Task.Delay(
                    _openAcknowledgementTimeout,
                    provisional.ExpirationCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!TryTakeProvisionalOpenLease(leaseId, provisional, out _))
        {
            return;
        }

        try
        {
            await ReleaseOpenLeaseAsync(leaseId, "expired provisional").ConfigureAwait(false);
        }
        finally
        {
            provisional.ExpirationCancellation.Dispose();
        }
    }

    private async Task<VfsIpcResponse> AcknowledgeOpenLeaseAsync(ulong leaseId, uint peerUserId)
    {
        if (!TryTakeProvisionalOpenLease(leaseId, expected: null, out var provisional, peerUserId))
        {
            return VfsIpcResponse.Error(VfsIpcStatus.IoError);
        }

        await CancelExpirationAsync(provisional).ConfigureAwait(false);
        return VfsIpcResponse.Success();
    }

    private async Task CancelProvisionalOpenLeaseAsync(ulong leaseId, uint peerUserId)
    {
        if (TryTakeProvisionalOpenLease(leaseId, expected: null, out var provisional, peerUserId))
        {
            await CancelExpirationAsync(provisional).ConfigureAwait(false);
        }
    }

    private static async Task CancelExpirationAsync(ProvisionalOpenLease provisional)
    {
        provisional.ExpirationCancellation.Cancel();
        try
        {
            await provisional.ExpirationTask.ConfigureAwait(false);
        }
        finally
        {
            provisional.ExpirationCancellation.Dispose();
        }
    }

    private bool TryTakeProvisionalOpenLease(
        ulong leaseId,
        ProvisionalOpenLease? expected,
        out ProvisionalOpenLease provisional,
        uint? peerUserId = null)
    {
        lock (_provisionalLeasesGate)
        {
            // A client may only acknowledge or release the provisional leases it opened itself.
            if (!_provisionalLeases.TryGetValue(leaseId, out provisional!) ||
                expected is not null && !ReferenceEquals(provisional, expected) ||
                peerUserId is { } userId && userId != provisional.OwnerUserId)
            {
                provisional = null!;
                return false;
            }

            _provisionalLeases.Remove(leaseId);
            return true;
        }
    }

    private async Task ReleaseUndeliveredOpenLeaseAsync(
        VfsIpcRequest request,
        VfsIpcResponse response)
    {
        if (!IsProvisionalOpenResponse(request, response) ||
            !TryTakeProvisionalOpenLease(response.LeaseId, expected: null, out var provisional))
        {
            return;
        }

        try
        {
            await CancelExpirationAsync(provisional).ConfigureAwait(false);
            await ReleaseOpenLeaseAsync(response.LeaseId, "undelivered").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<VfsIpcServer>(
                ex,
                $"[vfs-ipc] Failed releasing an undelivered Open lease: {ex.Message}");
        }
    }

    private async Task ReleaseOpenLeaseAsync(ulong leaseId, string reason)
    {
        try
        {
            await _requestHandler(VfsIpcRequest.Release(leaseId), ServerPeer, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunnerLog.Error<VfsIpcServer>(
                ex,
                $"[vfs-ipc] Failed releasing {reason} Open lease '{leaseId}': {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener?.Dispose();

        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Ignore accept loop failures during shutdown.
            }
        }

        Task[] clientTasks;
        lock (_clientTasksGate)
        {
            clientTasks = _clientTasks.ToArray();
        }

        try
        {
            await Task.WhenAll(clientTasks).ConfigureAwait(false);
        }
        catch
        {
            // Client handlers translate their own failures; shutdown is best effort.
        }

        ProvisionalOpenLease[] provisionalLeases;
        lock (_provisionalLeasesGate)
        {
            provisionalLeases = _provisionalLeases.Values.ToArray();
            _provisionalLeases.Clear();
        }

        foreach (var provisional in provisionalLeases)
        {
            await CancelExpirationAsync(provisional).ConfigureAwait(false);
            await ReleaseOpenLeaseAsync(
                    provisional.LeaseId,
                    "server-shutdown provisional")
                .ConfigureAwait(false);
        }

        Task[] expirationTasks;
        lock (_provisionalLeasesGate)
        {
            expirationTasks = _provisionalExpirationTasks.ToArray();
        }

        await Task.WhenAll(expirationTasks).ConfigureAwait(false);

        if (File.Exists(_socketPath))
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch
            {
                // Best effort cleanup.
            }
        }

        _clientSlots.Dispose();
        _shutdown.Dispose();
    }

    private sealed class ProvisionalOpenLease(
        ulong leaseId,
        uint ownerUserId,
        CancellationTokenSource expirationCancellation)
    {
        public ulong LeaseId { get; } = leaseId;
        public uint OwnerUserId { get; } = ownerUserId;
        public CancellationTokenSource ExpirationCancellation { get; } = expirationCancellation;
        public Task ExpirationTask { get; set; } = Task.CompletedTask;
    }
}

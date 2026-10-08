using Aria2NET;
using Aria2NET.Exceptions;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Offloadr.Runner.Core;

internal sealed class Aria2ProcessManager : IAsyncDisposable
{
    private readonly Aria2Settings _options;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly bool _traceRpc;
    private readonly bool _useProcessWatchdog;
    private readonly IAria2FileAccess? _fileAccess;
    private int _disposed;

    private Process? _process;
    private Task? _stdoutPump;
    private Task? _stderrPump;
    private Aria2NetClient? _client;
    private bool _started;

    public Aria2ProcessManager()
        : this(Aria2Settings.FromEnvironment())
    {
    }

    public Aria2ProcessManager(Aria2Settings options, bool useProcessWatchdog = true, IAria2FileAccess? fileAccess = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _useProcessWatchdog = useProcessWatchdog;
        _fileAccess = fileAccess;
        _traceRpc = string.Equals(Environment.GetEnvironmentVariable("ARIA2_RPC_TRACE"), "1", StringComparison.OrdinalIgnoreCase);
        _httpClient = CreateHttpClient();
    }

    public Aria2Settings Options => _options;

    public bool IsRunning => _process != null && !_process.HasExited;

    internal int? ProcessId => _process?.Id;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started) return;
        _started = true;

        try
        {
            Directory.CreateDirectory(_options.DownloadDirectory);
            Directory.CreateDirectory(_options.StateDirectory);
            EnsureSessionFileExists();
            WriteRpcConfig();
            _fileAccess?.PrepareStateDirectory(_options, RpcConfigPath);
            PrepareRestoredTargets();

            var psi = BuildStartInfo();
            var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start aria2c process.");
            }
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (!_pumpCancellation.IsCancellationRequested)
                {
                    RunnerLog.Error<Aria2ProcessManager>($"aria2 process exited unexpectedly with code {process.ExitCode}");
                }
            };

            _process = process;
            _stdoutPump = PumpOutputAsync(process.StandardOutput, line => RunnerLog.Info<Aria2ProcessManager>($"[aria2] {RedactForLog(line, _options.RpcSecret)}"), _pumpCancellation.Token);
            _stderrPump = PumpOutputAsync(process.StandardError, line => RunnerLog.Error<Aria2ProcessManager>($"[aria2] {RedactForLog(line, _options.RpcSecret)}"), _pumpCancellation.Token);

            _client = new Aria2NetClient(_options.RpcEndpoint, _options.RpcSecret, _httpClient);

            await WaitForReadyAsync(process, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<string> AddUriAsync(IEnumerable<string> uris, IDictionary<string, object>? options, int? position, CancellationToken cancellationToken)
    {
        if (uris is null) throw new ArgumentNullException(nameof(uris));
        var list = new List<string>();
        foreach (var uri in uris)
        {
            if (string.IsNullOrWhiteSpace(uri)) continue;
            list.Add(uri.Trim());
        }
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one URI is required.", nameof(uris));
        }

        var preparedOptions = PrepareOptions(options);
        return await GetClient().AddUriAsync(list, preparedOptions, position, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> AddMetalinkAsync(byte[] metalinkContent, IDictionary<string, object>? options, int? position, CancellationToken cancellationToken)
    {
        if (metalinkContent is null || metalinkContent.Length == 0)
        {
            throw new ArgumentException("Metalink content is required.", nameof(metalinkContent));
        }

        var preparedOptions = PrepareOptions(options);
        var result = await GetClient().AddMetalinkAsync(metalinkContent, preparedOptions, position, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<string> PauseAsync(string gid, CancellationToken cancellationToken)
    {
        return GetClient().PauseAsync(ValidateGid(gid), cancellationToken);
    }

    public Task<string> ForcePauseAsync(string gid, CancellationToken cancellationToken)
    {
        return GetClient().ForcePauseAsync(ValidateGid(gid), cancellationToken);
    }

    public Task<string> ForceRemoveAsync(string gid, CancellationToken cancellationToken)
    {
        return GetClient().ForceRemoveAsync(ValidateGid(gid), cancellationToken);
    }

    public Task RemoveDownloadResultAsync(string gid, CancellationToken cancellationToken)
    {
        return GetClient().RemoveDownloadResultAsync(ValidateGid(gid), cancellationToken);
    }

    public Task<string> UnpauseAsync(string gid, CancellationToken cancellationToken)
    {
        return GetClient().UnpauseAsync(ValidateGid(gid), cancellationToken);
    }

    public Task<bool> PauseAllAsync(CancellationToken cancellationToken)
    {
        return GetClient().PauseAllAsync(cancellationToken);
    }

    public Task<bool> UnpauseAllAsync(CancellationToken cancellationToken)
    {
        return GetClient().UnpauseAllAsync(cancellationToken);
    }

    public Task<DownloadStatusResult> GetStatusAsync(string gid, CancellationToken cancellationToken)
    {
        return GetClient().TellStatusAsync(ValidateGid(gid), cancellationToken);
    }

    public async Task<IReadOnlyList<DownloadStatusResult>> ListAsync(CancellationToken cancellationToken)
    {
        var result = await GetClient().TellAllAsync(cancellationToken).ConfigureAwait(false);
        return result.ToArray();
    }

    public async Task ChangeUriAsync(
        string gid,
        IReadOnlyList<string> currentUris,
        IReadOnlyList<string> replacementUris,
        CancellationToken cancellationToken)
    {
        await GetClient()
            .ChangeUriAsync(
                ValidateGid(gid),
                fileIndex: 1,
                currentUris.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                replacementUris.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray(),
                position: 0,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task MoveToFrontAsync(string gid, CancellationToken cancellationToken)
    {
        await InvokeRawRpcAsync(
                "aria2.changePosition",
                [ValidateGid(gid), 0, "POS_SET"],
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(CancellationToken cancellationToken)
    {
        await InvokeRawRpcAsync("aria2.saveSession", [], cancellationToken).ConfigureAwait(false);
    }

    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_client is null) return;
        try
        {
            await _client.ShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Aria2Exception ex) when (ex.ResultCode == -32600)
        {
            // RPC not fully initialised; safe to ignore when tearing down during failed start.
        }
        catch (Exception ex)
        {
            RunnerLog.Error<Aria2ProcessManager>(ex, $"Failed to shutdown aria2 gracefully: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pumpCancellation.Cancel();

        try
        {
            using var cts = new CancellationTokenSource(_options.ShutdownTimeout);
            await ShutdownAsync(cts.Token).ConfigureAwait(false);
        }
        catch
        {
            // ignore
        }

        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore cleanup exceptions
            }
        }

        if (_stdoutPump != null)
        {
            try { await _stdoutPump.ConfigureAwait(false); } catch { }
        }
        if (_stderrPump != null)
        {
            try { await _stderrPump.ConfigureAwait(false); } catch { }
        }

        _process?.Dispose();
        _stdoutPump = null;
        _stderrPump = null;
        _process = null;

        _httpClient.Dispose();
        _pumpCancellation.Dispose();
    }

    internal ProcessStartInfo BuildStartInfo()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _options.BinaryPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = _options.StateDirectory
        };

        // aria2 needs none of the agent's configuration, which includes the runner secret.
        var inherited = psi.Environment.ToArray();
        psi.Environment.Clear();
        foreach (var (name, value) in inherited)
        {
            if (InheritedEnvironment.Contains(name))
            {
                psi.Environment[name] = value;
            }
        }

        if (_fileAccess?.Account is { } account)
        {
            psi.UserName = account.UserName;
            psi.Environment["HOME"] = _options.StateDirectory;
            psi.Environment["USER"] = account.UserName;
            psi.Environment["LOGNAME"] = account.UserName;
        }

        foreach (var arg in BuildArguments())
        {
            psi.ArgumentList.Add(arg);
        }

        return psi;
    }

    private static readonly HashSet<string> InheritedEnvironment = new(StringComparer.Ordinal)
    {
        "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL", "TZ",
        "http_proxy", "https_proxy", "ftp_proxy", "all_proxy", "no_proxy",
        "HTTP_PROXY", "HTTPS_PROXY", "FTP_PROXY", "ALL_PROXY", "NO_PROXY",
        "SSL_CERT_FILE", "SSL_CERT_DIR"
    };

    private IEnumerable<string> BuildArguments()
    {
        // The RPC secret is read from a file only aria2's account and root can read: argv is
        // visible to every process.
        yield return $"--conf-path={RpcConfigPath}";
        yield return "--enable-rpc=true";
        yield return "--rpc-listen-all=false";
        yield return $"--rpc-listen-port={_options.RpcPort}";
        yield return $"--dir={_options.DownloadDirectory}";
        yield return $"--save-session={_options.SessionFilePath}";
        yield return $"--save-session-interval={_options.SaveSessionInterval}";
        yield return $"--auto-save-interval={_options.AutoSaveInterval}";
        yield return "--max-concurrent-downloads=1";
        if (_useProcessWatchdog)
        {
            yield return $"--stop-with-process={Environment.ProcessId}";
        }

        yield return "--continue=true";
        if (_options.DisableIpv6)
        {
            yield return "--disable-ipv6=true";
        }
        if (_options.CheckIntegrity)
        {
            yield return "--check-integrity=true";
        }
        if (File.Exists(_options.SessionFilePath))
        {
            yield return $"--input-file={_options.SessionFilePath}";
        }
        // RunnerAgent reports transfer progress and failures through the RPC state.
        // Suppress aria2's per-connection console diagnostics, which may include
        // signed redirect URLs and are not authoritative for the overall transfer.
        yield return "--quiet=true";
        yield return "--enable-color=false";
    }

    private HttpClient CreateHttpClient()
    {
        var socketsHandler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(3)
        };

        HttpMessageHandler handler = socketsHandler;
        if (_traceRpc)
        {
            handler = new RpcHttpHandler(handler, _options.RpcSecret, trace: true);
        }
        else
        {
            handler = new RpcHttpHandler(handler, _options.RpcSecret, trace: false);
        }

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    internal string RpcConfigPath => Path.Combine(_options.StateDirectory, "rpc.conf");

    internal void WriteRpcConfig()
    {
        if (_options.RpcSecret.AsSpan().ContainsAny('\r', '\n'))
        {
            throw new InvalidOperationException("The aria2 RPC secret must be a single line.");
        }

        File.Delete(RpcConfigPath);
        var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var writer = new StreamWriter(RpcConfigPath, fileOptions);
        writer.Write($"rpc-secret={_options.RpcSecret}\n");
    }

    /// <summary>
    /// aria2 resumes the transfers in its session file as soon as it starts. Their targets were
    /// prepared when they were created, unless an earlier version ran aria2 as root.
    /// </summary>
    private void PrepareRestoredTargets()
    {
        if (_fileAccess?.Account is null || !File.Exists(_options.SessionFilePath))
        {
            return;
        }

        foreach (var target in ReadSessionTargets(File.ReadLines(_options.SessionFilePath)))
        {
            try
            {
                _fileAccess.PrepareTransferTarget(target);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                RunnerLog.Warning<Aria2ProcessManager>(
                    $"Could not prepare restored aria2 transfer target '{target}': {ex.Message}");
            }
        }
    }

    /// <summary>Reads the dir/out target of each entry in an aria2 session file.</summary>
    internal static IEnumerable<string> ReadSessionTargets(IEnumerable<string> lines)
    {
        string? directory = null;
        string? output = null;
        foreach (var line in lines.Append(string.Empty))
        {
            if (line.Length > 0 && char.IsWhiteSpace(line[0]))
            {
                var option = line.Trim();
                if (option.StartsWith("dir=", StringComparison.Ordinal))
                {
                    directory = option["dir=".Length..];
                }
                else if (option.StartsWith("out=", StringComparison.Ordinal))
                {
                    output = option["out=".Length..];
                }

                continue;
            }

            if (!string.IsNullOrEmpty(directory) && !string.IsNullOrEmpty(output) && Path.IsPathRooted(directory))
            {
                yield return Path.Combine(directory, output);
            }

            directory = null;
            output = null;
        }
    }

    private void EnsureSessionFileExists()
    {
        var dir = Path.GetDirectoryName(_options.SessionFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (!File.Exists(_options.SessionFilePath))
        {
            using var _ = File.Create(_options.SessionFilePath);
        }
    }

    private Aria2NetClient GetClient()
    {
        return _client ?? throw new InvalidOperationException("aria2 RPC client is not ready. Call StartAsync first.");
    }

    private async Task InvokeRawRpcAsync(
        string method,
        IReadOnlyList<object> parameters,
        CancellationToken cancellationToken)
    {
        var rpcParameters = new List<object>(parameters.Count + 1)
        {
            $"token:{_options.RpcSecret}"
        };
        rpcParameters.AddRange(parameters);

        var payload = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = Guid.NewGuid().ToString("n"),
            method,
            @params = rpcParameters
        });

        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _httpClient
            .PostAsync(_options.RpcEndpoint, content, cancellationToken)
            .ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureRawRpcResponse(method, response, responseBody);
    }

    internal static void EnsureRawRpcResponse(
        string method,
        HttpResponseMessage response,
        string responseBody)
    {
        ArgumentNullException.ThrowIfNull(response);

        Aria2RawRpcException? rpcException;
        try
        {
            rpcException = ParseRawRpcError(method, responseBody);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            response.EnsureSuccessStatusCode();
            throw;
        }

        if (rpcException is not null)
        {
            throw rpcException;
        }

        response.EnsureSuccessStatusCode();
    }

    private static Aria2RawRpcException? ParseRawRpcError(string method, string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (!document.RootElement.TryGetProperty("error", out var error))
        {
            return null;
        }

        var resultCode = error.TryGetProperty("code", out var codeElement) &&
                         codeElement.TryGetInt64(out var parsedCode)
            ? parsedCode
            : 0;
        var resultMessage = error.TryGetProperty("message", out var messageElement) &&
                            messageElement.ValueKind == JsonValueKind.String
            ? messageElement.GetString()
            : null;
        return new Aria2RawRpcException(method, resultCode, resultMessage, error.ToString());
    }

    private static string ValidateGid(string gid)
    {
        if (string.IsNullOrWhiteSpace(gid))
        {
            throw new ArgumentException("GID is required.", nameof(gid));
        }
        return gid.Trim();
    }

    private async Task WaitForReadyAsync(Process process, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _options.ReadyTimeout;
        Exception? lastError = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException($"aria2 process exited before RPC became ready (code {process.ExitCode}).");
            }

            try
            {
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probeCts.CancelAfter(TimeSpan.FromSeconds(2));
                var version = await GetClient().GetVersionAsync(probeCts.Token).ConfigureAwait(false);
                RunnerLog.Info<Aria2ProcessManager>($"aria2 RPC ready (version {version.Version}).");
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = null;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException("Timed out waiting for aria2 RPC to become ready.", lastError);
            }

            try
            {
                await Task.Delay(_options.ProbeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private IDictionary<string, object> PrepareOptions(IDictionary<string, object>? options)
    {
        var prepared = options != null
            ? new Dictionary<string, object>(options, StringComparer.Ordinal)
            : new Dictionary<string, object>(StringComparer.Ordinal);

        if (!prepared.ContainsKey("dir"))
        {
            prepared["dir"] = _options.DownloadDirectory;
        }
        if (!prepared.ContainsKey("continue"))
        {
            prepared["continue"] = true;
        }
        if (!prepared.ContainsKey("check-integrity") && _options.CheckIntegrity)
        {
            prepared["check-integrity"] = true;
        }

        return prepared;
    }

    private static Task PumpOutputAsync(StreamReader reader, Action<string> log, CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length == 0) continue;
                    log(line);
                }
            }
            catch (IOException)
            {
                // stream closed
            }
            catch (ObjectDisposedException)
            {
                // stream disposed during shutdown
            }
        }, CancellationToken.None);
    }

    private sealed class RpcHttpHandler : DelegatingHandler
    {
        private readonly string _secret;
        private readonly bool _trace;

        public RpcHttpHandler(HttpMessageHandler inner, string secret, bool trace) : base(inner)
        {
            _secret = secret;
            _trace = trace;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null && request.Content.Headers.ContentType == null)
            {
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = Encoding.UTF8.WebName
                };
            }

            if (_trace && request.Content != null)
            {
                var payload = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                RunnerLog.Info<Aria2ProcessManager>($"[aria2-rpc] -> {RedactForLog(payload, _secret)}");
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (_trace && response.Content != null)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                RunnerLog.Info<Aria2ProcessManager>($"[aria2-rpc] <- {RedactForLog(body, _secret)}");
                response.Content = new StringContent(body, Encoding.UTF8, response.Content.Headers.ContentType?.MediaType ?? "application/json");
            }

            return response;
        }
    }

    // Any scheme://authority/... run up to whitespace, a quote or an angle bracket. aria2 escapes
    // '/' as \/ in its JSON, and JSON escapes such as & stay inside the match so no part of
    // a query string survives.
    private static readonly Regex UriPattern = new(
        @"\b(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*):(?<sep>//|\\/\\/)(?<authority>[^/\s""'<>?#\\]*)[^\s""'<>]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Makes aria2 RPC payloads and console lines safe to log: download URIs are usually signed,
    /// so each one keeps only its scheme and host, and the RPC secret is masked.
    /// </summary>
    internal static string RedactForLog(string text, string? secret)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        if (!string.IsNullOrEmpty(secret))
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }

        return UriPattern.Replace(text, match =>
        {
            var authority = match.Groups["authority"].Value;
            var at = authority.LastIndexOf('@');
            var host = at >= 0 ? authority[(at + 1)..] : authority;
            return $"{match.Groups["scheme"].Value}:{match.Groups["sep"].Value}{host}/<redacted>";
        });
    }
}

internal sealed class Aria2RawRpcException(
    string method,
    long resultCode,
    string? resultMessage,
    string rawError)
    : InvalidOperationException($"aria2 RPC '{method}' failed: {rawError}")
{
    public long ResultCode { get; } = resultCode;
    public string? ResultMessage { get; } = resultMessage;
}

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Offloadr.Runner.Core;

public readonly record struct ModelTransferIdentity(
    string ModelId,
    string DestinationPath,
    long ExpectedLength,
    string Digest,
    string DeterministicIdentifier)
{
    private const string IdentityVersion = "model-transfer-v1";

    public static ModelTransferIdentity Create(string? modelId, string destinationPath, long expectedLength)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException("Range-managed model transfers require model_id.");
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new InvalidOperationException("Range-managed model transfers require a destination path.");
        }

        if (expectedLength <= 0)
        {
            throw new InvalidOperationException("Range-managed model transfers require a positive expected length.");
        }

        var normalizedModelId = modelId.Trim();
        var normalizedDestination = Path.GetFullPath(destinationPath.Trim());
        var canonical = string.Join(
            '\n',
            IdentityVersion,
            normalizedModelId,
            normalizedDestination,
            expectedLength.ToString(CultureInfo.InvariantCulture));
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new ModelTransferIdentity(
            normalizedModelId,
            normalizedDestination,
            expectedLength,
            digest,
            digest[..16]);
    }
}

public sealed record ModelTransferIndexRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required string IdentityDigest { get; init; }
    public required string ModelId { get; init; }
    public required string DestinationPath { get; init; }
    public required long ExpectedLength { get; init; }
    public required string Handle { get; init; }
    public long TransferEpoch { get; init; } = 1;
    public long PieceLength { get; init; }
    public long NumPieces { get; init; }
    public ulong DeviceId { get; init; }
    public ulong Inode { get; init; }
    public bool PlaceholderOwned { get; init; }
    public bool Complete { get; init; }
    public bool RestartRequired { get; init; }
}

public sealed class ModelTransferIndex
{
    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;
    private const int OpenCloseOnExec = 0x80000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _indexDirectory;
    private readonly string _quarantineDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, ModelTransferIndexRecord> _records = new(StringComparer.Ordinal);

    public ModelTransferIndex(string stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new ArgumentException("Transfer state directory is required.", nameof(stateDirectory));
        }

        _indexDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "model-transfers");
        _quarantineDirectory = Path.Combine(_indexDirectory, "quarantine");
        Directory.CreateDirectory(_indexDirectory);
        FlushDirectory(Path.GetDirectoryName(_indexDirectory)!);
        Directory.CreateDirectory(_quarantineDirectory);
        FlushDirectory(_indexDirectory);
        Load();
    }

    public IReadOnlyList<ModelTransferIndexRecord> Snapshot()
    {
        lock (_records)
        {
            return _records.Values.ToArray();
        }
    }

    public bool TryGet(string identityDigest, out ModelTransferIndexRecord record)
    {
        lock (_records)
        {
            return _records.TryGetValue(identityDigest, out record!);
        }
    }

    public async Task UpsertAsync(ModelTransferIndexRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetRecordPath(record.IdentityDigest);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await WriteTemporaryRecordAsync(temporaryPath, record, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, path, overwrite: true);
                FlushDirectory(_indexDirectory);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            lock (_records)
            {
                _records[record.IdentityDigest] = record;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Upsert(ModelTransferIndexRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);

        _writeGate.Wait();
        try
        {
            var path = GetRecordPath(record.IdentityDigest);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                WriteTemporaryRecord(temporaryPath, record);
                File.Move(temporaryPath, path, overwrite: true);
                FlushDirectory(_indexDirectory);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            lock (_records)
            {
                _records[record.IdentityDigest] = record;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task RemoveAsync(string identityDigest, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = GetRecordPath(identityDigest);
            if (File.Exists(path))
            {
                File.Delete(path);
                FlushDirectory(_indexDirectory);
            }

            lock (_records)
            {
                _records.Remove(identityDigest);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task QuarantineAsync(
        ModelTransferIndexRecord record,
        string reason,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sourcePath = GetRecordPath(record.IdentityDigest);
            if (File.Exists(sourcePath))
            {
                var safeReason = string.Concat(reason.Where(static value => char.IsLetterOrDigit(value) || value is '-' or '_'));
                var destination = Path.Combine(
                    _quarantineDirectory,
                    $"{DateTime.UtcNow:yyyyMMddHHmmssfff}-{record.IdentityDigest}-{safeReason}.json");
                File.Move(sourcePath, destination, overwrite: false);
                FlushDirectory(_quarantineDirectory);
                FlushDirectory(_indexDirectory);
            }

            lock (_records)
            {
                _records.Remove(record.IdentityDigest);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private void Load()
    {
        foreach (var path in Directory.EnumerateFiles(_indexDirectory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ModelTransferIndexRecord>(File.ReadAllText(path), JsonOptions);
                if (record is null)
                {
                    continue;
                }

                Validate(record);
                _records[record.IdentityDigest] = record;
            }
            catch (Exception ex)
            {
                RunnerLog.Warning<ModelTransferIndex>($"Ignoring invalid model transfer index '{path}': {ex.Message}");
            }
        }
    }

    private string GetRecordPath(string identityDigest)
        => Path.Combine(_indexDirectory, $"{identityDigest}.json");

    private static async Task WriteTemporaryRecordAsync(
        string path,
        ModelTransferIndexRecord record,
        CancellationToken cancellationToken)
    {
        var contents = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions));
        await using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.WriteThrough
            });
        await stream.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void WriteTemporaryRecord(string path, ModelTransferIndexRecord record)
    {
        var contents = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions));
        using var stream = new FileStream(
            path,
            new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough
            });
        stream.Write(contents);
        stream.Flush(flushToDisk: true);
    }

    private static void FlushDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var descriptor = open(path, OpenReadOnly | OpenDirectory | OpenCloseOnExec);
        if (descriptor < 0)
        {
            throw new IOException(
                $"Could not open model transfer index directory '{path}' for durable flush.",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            if (fsync(descriptor) != 0)
            {
                throw new IOException(
                    $"Could not durably flush model transfer index directory '{path}'.",
                    new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            _ = close(descriptor);
        }
    }

    private static void Validate(ModelTransferIndexRecord record)
    {
        if (record.SchemaVersion != 1 ||
            record.IdentityDigest.Length != 64 ||
            string.IsNullOrWhiteSpace(record.ModelId) ||
            string.IsNullOrWhiteSpace(record.DestinationPath) ||
            record.ExpectedLength <= 0 ||
            string.IsNullOrWhiteSpace(record.Handle) ||
            record.TransferEpoch <= 0)
        {
            throw new InvalidDataException("Model transfer index record is invalid.");
        }

        var identity = ModelTransferIdentity.Create(
            record.ModelId,
            record.DestinationPath,
            record.ExpectedLength);
        if (!string.Equals(identity.Digest, record.IdentityDigest, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Model transfer index identity digest is invalid.");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DllImport("libc")]
    private static extern int close(int descriptor);
}

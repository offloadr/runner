using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Offloadr.Runner.V1;

namespace Offloadr.Runner.Linux;

/// <summary>
/// Fingerprints an opened snapshot locally so it keeps its server-issued upload ID.
/// The fingerprint never crosses the wire and contains no plaintext content digest.
/// Its uploader namespace prevents a replacement from adopting the old binding.
/// </summary>
internal sealed class ArtifactUploadOperationIdentity
{
    private readonly byte[] _namespace = Guid.NewGuid().ToByteArray();

    public string ForSnapshot(RunnerArtifactUploadMetadata metadata, SecureFileVersion version)
    {
        // Transfer encryption is deliberately fresh on every attempt. It cannot own the
        // logical snapshot identity, nor can an existing ID feed back into its derivation.
        var binding = metadata.Clone();
        binding.Envelope = null;
        binding.UploadId = string.Empty;
        binding.Sha256 = null;

        Span<byte> fileVersion = stackalloc byte[32];
        BinaryPrimitives.WriteUInt64LittleEndian(fileVersion, version.Inode);
        BinaryPrimitives.WriteInt64LittleEndian(fileVersion[8..], version.Status.Length);
        BinaryPrimitives.WriteInt64LittleEndian(fileVersion[16..], version.Status.LastWriteTimeUtc.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(fileVersion[24..], version.ChangeTimeUtc.Ticks);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_namespace);
        hash.AppendData(fileVersion);
        hash.AppendData(binding.ToByteArray());
        return new Guid(hash.GetHashAndReset().AsSpan(0, 16)).ToString("N");
    }
}

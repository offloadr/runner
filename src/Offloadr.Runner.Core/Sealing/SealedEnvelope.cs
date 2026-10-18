using System.Security.Cryptography;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// The key material that travels with a sealed payload, field for field as in the transfer
/// metadata: the canonical header, the HPKE encapsulated key and the HPKE-sealed data key.
/// See docs/sealed-envelope.md.
/// </summary>
internal sealed record SealedEnvelope(
    ReadOnlyMemory<byte> Header,
    ReadOnlyMemory<byte> EncapsulatedKey,
    ReadOnlyMemory<byte> SealedDataKey);

/// <summary>Constants of format version 1 (docs/sealed-envelope.md).</summary>
internal static class SealedEnvelopeFormat
{
    /// <summary>The only algorithm id a sealing public key may carry.</summary>
    public const string AlgorithmId = "hpke-p256-sha256-aes128gcm+stream-aes256gcm-64k-v1";

    public const ushort Version = 1;

    /// <summary>Payload AEAD id in the header: AES-256-GCM STREAM with 64 KiB chunks.</summary>
    public const ushort PayloadId = 0x0001;

    public const int DataKeySize = 32;
    public const int SealedDataKeySize = DataKeySize + Hpke.TagSize;
    public const int ChunkSize = 64 * 1024;
    public const int TagSize = 16;
    public const int ChunkNonceSize = 12;
    public const long MaxPlaintextSize = 4L * 1024 * 1024 * 1024;

    /// <summary>Chunks in the largest allowed payload; chunk counters are always below this.</summary>
    public const long MaxChunkCount = MaxPlaintextSize / ChunkSize;

    /// <summary>HPKE info for sealing the data key.</summary>
    public static ReadOnlySpan<byte> HpkeInfo => "offloadr sealed v1"u8;

    /// <summary>Number of payload chunks: an empty payload is one empty final chunk.</summary>
    public static long ChunkCount(long plaintextSize)
        => plaintextSize == 0 ? 1 : ((plaintextSize - 1) / ChunkSize) + 1;

    /// <summary>Plaintext bytes in chunk <paramref name="index"/>: full chunks except the last.</summary>
    public static int ChunkPlaintextLength(long plaintextSize, long index)
        => index < ChunkCount(plaintextSize) - 1 ? ChunkSize : (int)(plaintextSize - (index * ChunkSize));

    /// <summary>Total ciphertext length of a payload: the plaintext plus one tag per chunk.</summary>
    public static long CiphertextSize(long plaintextSize)
    {
        ValidatePlaintextSize(plaintextSize);
        return plaintextSize + (ChunkCount(plaintextSize) * TagSize);
    }

    public static void ValidatePlaintextSize(long plaintextSize)
    {
        if (plaintextSize < 0 || plaintextSize > MaxPlaintextSize)
        {
            throw new SealedEnvelopeException($"Plaintext size {plaintextSize} is outside 0..{MaxPlaintextSize} bytes.");
        }
    }

    /// <summary>STREAM nonce: an 11-byte big-endian chunk counter, then 0x01 on the final chunk and 0x00 otherwise.</summary>
    public static void ChunkNonce(long counter, bool final, Span<byte> nonce)
    {
        if (counter < 0 || counter >= MaxChunkCount)
        {
            throw new SealedEnvelopeException("Payload chunk counter overflow.");
        }

        nonce[..ChunkNonceSize].Clear();
        var value = (ulong)counter;
        for (var i = ChunkNonceSize - 2; i >= 0 && value != 0; i--)
        {
            nonce[i] = (byte)value;
            value >>= 8;
        }

        nonce[ChunkNonceSize - 1] = final ? (byte)0x01 : (byte)0x00;
    }
}

/// <summary>A sealed envelope or payload is malformed, does not match what was expected, or fails authentication.</summary>
internal sealed class SealedEnvelopeException : CryptographicException
{
    public SealedEnvelopeException(string message)
        : base(message)
    {
    }

    public SealedEnvelopeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// Seals one plaintext of known length to a recipient. Each writer has its own fresh data key
/// and HPKE encapsulation and encrypts exactly once: a retry or re-upload needs a new writer.
/// </summary>
internal sealed class SealedEnvelopeWriter : IDisposable
{
    private readonly byte[] dataKey;
    private bool started;
    private bool disposed;

    private SealedEnvelopeWriter(SealedEnvelopeHeader header, SealedEnvelope envelope, byte[] dataKey)
    {
        Header = header;
        Envelope = envelope;
        this.dataKey = dataKey;
    }

    public SealedEnvelopeHeader Header { get; }

    /// <summary>Send this ahead of the ciphertext.</summary>
    public SealedEnvelope Envelope { get; }

    /// <summary>Total ciphertext bytes the payload will have.</summary>
    public long CiphertextSize => SealedEnvelopeFormat.CiphertextSize(Header.PlaintextSize);

    public static SealedEnvelopeWriter Create(SealingPublicKey recipient, SealedObjectIdentity identity, long plaintextSize)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(identity);
        var header = new SealedEnvelopeHeader(recipient.KeyId, plaintextSize, identity);
        var headerBytes = header.Encode();
        var dataKey = RandomNumberGenerator.GetBytes(SealedEnvelopeFormat.DataKeySize);
        try
        {
            var sealedDataKey = Hpke.Seal(
                recipient.PublicKey.Span,
                SealedEnvelopeFormat.HpkeInfo,
                headerBytes,
                dataKey,
                out var encapsulatedKey);
            return new SealedEnvelopeWriter(header, new SealedEnvelope(headerBytes, encapsulatedKey, sealedDataKey), dataKey);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }
    }

    /// <summary>
    /// Reads exactly <see cref="SealedEnvelopeHeader.PlaintextSize"/> bytes from
    /// <paramref name="plaintext"/> and yields one ciphertext chunk (plaintext chunk plus tag) at a
    /// time. Every yielded segment is a new array the caller may keep. Throws
    /// <see cref="SealedEnvelopeException"/> when the plaintext is shorter or longer than declared.
    /// </summary>
    public IAsyncEnumerable<ReadOnlyMemory<byte>> EncryptChunksAsync(Stream plaintext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        Start();
        return EncryptChunksCoreAsync(plaintext, cancellationToken);
    }

    /// <summary>Encrypts the whole plaintext to <paramref name="ciphertext"/>.</summary>
    public async Task EncryptToAsync(Stream plaintext, Stream ciphertext, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        await foreach (var chunk in EncryptChunksAsync(plaintext, cancellationToken).ConfigureAwait(false))
        {
            await ciphertext.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CryptographicOperations.ZeroMemory(dataKey);
    }

    private void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (started)
        {
            throw new InvalidOperationException("A sealed envelope writer encrypts once; create a new writer for every attempt.");
        }

        started = true;
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> EncryptChunksCoreAsync(
        Stream plaintext,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var plaintextSize = Header.PlaintextSize;
        var chunkCount = SealedEnvelopeFormat.ChunkCount(plaintextSize);
        var buffer = new byte[SealedEnvelopeFormat.ChunkSize];
        var probe = new byte[1];
        try
        {
            using var aes = new AesGcm(dataKey, SealedEnvelopeFormat.TagSize);
            for (long index = 0; index < chunkCount; index++)
            {
                var length = SealedEnvelopeFormat.ChunkPlaintextLength(plaintextSize, index);
                var read = await plaintext.ReadAtLeastAsync(buffer.AsMemory(0, length), length, throwOnEndOfStream: false, cancellationToken)
                    .ConfigureAwait(false);
                if (read < length)
                {
                    throw new SealedEnvelopeException(
                        $"Plaintext ended after {(index * SealedEnvelopeFormat.ChunkSize) + read} of {plaintextSize} declared bytes.");
                }

                var final = index == chunkCount - 1;
                if (final && await plaintext.ReadAsync(probe, cancellationToken).ConfigureAwait(false) != 0)
                {
                    // Checked before the final chunk leaves, so a consumer never sees a complete payload.
                    throw new SealedEnvelopeException($"Plaintext is longer than the declared {plaintextSize} bytes.");
                }

                var chunk = new byte[length + SealedEnvelopeFormat.TagSize];
                EncryptChunk(aes, index, final, buffer.AsSpan(0, length), chunk);
                yield return chunk;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            Dispose();
        }
    }

    private static void EncryptChunk(AesGcm aes, long index, bool final, ReadOnlySpan<byte> plaintext, Span<byte> chunk)
    {
        Span<byte> nonce = stackalloc byte[SealedEnvelopeFormat.ChunkNonceSize];
        SealedEnvelopeFormat.ChunkNonce(index, final, nonce);
        aes.Encrypt(nonce, plaintext, chunk[..plaintext.Length], chunk[plaintext.Length..]);
    }
}

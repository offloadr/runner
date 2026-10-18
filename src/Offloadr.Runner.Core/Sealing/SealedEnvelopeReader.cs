using System.Security.Cryptography;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// Opens one sealed payload. Ciphertext may arrive in pieces of any size (as transport chunks
/// arrive); each payload chunk is written to the output only after its tag verifies. The payload
/// is valid only once <see cref="CompleteAsync"/> succeeds, so callers decrypt into a temporary
/// file and publish it only then. Any failure leaves the reader unusable.
/// </summary>
internal sealed class SealedEnvelopeReader : IDisposable
{
    private readonly byte[] dataKey;
    private readonly AesGcm aes;
    private readonly Stream output;
    private readonly long chunkCount;
    private readonly byte[] ciphertextChunk = new byte[SealedEnvelopeFormat.ChunkSize + SealedEnvelopeFormat.TagSize];
    private readonly byte[] plaintextChunk = new byte[SealedEnvelopeFormat.ChunkSize];
    private long chunkIndex;
    private int buffered;
    private bool completed;
    private bool failed;
    private bool disposed;

    private SealedEnvelopeReader(SealedEnvelopeHeader header, byte[] dataKey, Stream output)
    {
        Header = header;
        this.dataKey = dataKey;
        this.output = output;
        aes = new AesGcm(dataKey, SealedEnvelopeFormat.TagSize);
        chunkCount = SealedEnvelopeFormat.ChunkCount(header.PlaintextSize);
    }

    public SealedEnvelopeHeader Header { get; }

    /// <summary>Verified plaintext bytes written to the output so far.</summary>
    public long PlaintextBytesWritten { get; private set; }

    /// <summary>
    /// Validates the envelope against what was requested and opens its data key. Throws
    /// <see cref="SealedEnvelopeException"/> when the header is malformed, is sealed to another
    /// key id, names another object than <paramref name="expectedIdentity"/>, or does not open.
    /// </summary>
    public static SealedEnvelopeReader Open(
        SealedEnvelope envelope,
        SealingKeyPair recipient,
        SealedObjectIdentity expectedIdentity,
        Stream plaintextOutput)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        ArgumentNullException.ThrowIfNull(plaintextOutput);

        var header = SealedEnvelopeHeader.Parse(envelope.Header.Span);
        if (!string.Equals(header.KeyId, recipient.KeyId, StringComparison.Ordinal))
        {
            throw new SealedEnvelopeException("Envelope is sealed to a different key id.");
        }

        if (header.Identity != expectedIdentity)
        {
            throw new SealedEnvelopeException("Envelope belongs to a different object than the one requested.");
        }

        if (envelope.EncapsulatedKey.Length != Hpke.EncapsulatedKeySize
            || envelope.SealedDataKey.Length != SealedEnvelopeFormat.SealedDataKeySize)
        {
            throw new SealedEnvelopeException("Envelope key material has the wrong length.");
        }

        byte[] dataKey;
        try
        {
            dataKey = Hpke.Open(
                recipient.Key,
                recipient.PublicKey.PublicKey.Span,
                envelope.EncapsulatedKey.Span,
                SealedEnvelopeFormat.HpkeInfo,
                envelope.Header.Span,
                envelope.SealedDataKey.Span);
        }
        catch (SealedEnvelopeException)
        {
            throw;
        }
        catch (CryptographicException ex)
        {
            throw new SealedEnvelopeException("Envelope data key could not be opened.", ex);
        }

        try
        {
            return new SealedEnvelopeReader(header, dataKey, plaintextOutput);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(dataKey);
            throw;
        }
    }

    /// <summary>Consumes the next piece of ciphertext, writing every chunk it completes.</summary>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            while (!ciphertext.IsEmpty)
            {
                if (chunkIndex == chunkCount)
                {
                    throw new SealedEnvelopeException("Ciphertext continues after the final chunk.");
                }

                var chunkLength = CurrentChunkCiphertextLength();
                var take = Math.Min(chunkLength - buffered, ciphertext.Length);
                ciphertext.Span[..take].CopyTo(ciphertextChunk.AsSpan(buffered));
                buffered += take;
                ciphertext = ciphertext[take..];
                if (buffered == chunkLength)
                {
                    var plaintextLength = DecryptChunk(chunkLength);
                    await output.WriteAsync(plaintextChunk.AsMemory(0, plaintextLength), cancellationToken).ConfigureAwait(false);
                    PlaintextBytesWritten += plaintextLength;
                    chunkIndex++;
                    buffered = 0;
                }
            }
        }
        catch
        {
            Fail();
            throw;
        }
    }

    /// <summary>
    /// Verifies the payload ended exactly after its final chunk and flushes the output. Throws
    /// <see cref="SealedEnvelopeException"/> when the ciphertext was truncated.
    /// </summary>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureWritable();
        try
        {
            if (chunkIndex != chunkCount || buffered != 0)
            {
                throw new SealedEnvelopeException("Ciphertext ended before the final chunk.");
            }

            if (PlaintextBytesWritten != Header.PlaintextSize)
            {
                throw new SealedEnvelopeException("Decrypted size does not match the sealed plaintext size.");
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
        }
        catch
        {
            Fail();
            throw;
        }
        finally
        {
            ZeroKeys();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ZeroKeys();
        CryptographicOperations.ZeroMemory(ciphertextChunk);
        CryptographicOperations.ZeroMemory(plaintextChunk);
    }

    private int CurrentChunkCiphertextLength()
        => SealedEnvelopeFormat.ChunkPlaintextLength(Header.PlaintextSize, chunkIndex) + SealedEnvelopeFormat.TagSize;

    private int DecryptChunk(int chunkLength)
    {
        Span<byte> nonce = stackalloc byte[SealedEnvelopeFormat.ChunkNonceSize];
        SealedEnvelopeFormat.ChunkNonce(chunkIndex, chunkIndex == chunkCount - 1, nonce);
        var plaintextLength = chunkLength - SealedEnvelopeFormat.TagSize;
        try
        {
            aes.Decrypt(
                nonce,
                ciphertextChunk.AsSpan(0, plaintextLength),
                ciphertextChunk.AsSpan(plaintextLength, SealedEnvelopeFormat.TagSize),
                plaintextChunk.AsSpan(0, plaintextLength));
        }
        catch (CryptographicException ex)
        {
            throw new SealedEnvelopeException($"Payload chunk {chunkIndex} failed authentication.", ex);
        }

        return plaintextLength;
    }

    private void EnsureWritable()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (failed)
        {
            throw new SealedEnvelopeException("Sealed payload already failed validation.");
        }

        if (completed)
        {
            throw new InvalidOperationException("Sealed payload is already complete.");
        }
    }

    private void Fail()
    {
        failed = true;
        ZeroKeys();
    }

    private void ZeroKeys()
    {
        CryptographicOperations.ZeroMemory(dataKey);
        aes.Dispose();
    }
}

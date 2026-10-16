using Offloadr.Runner.Core.Sealing;
using Offloadr.Runner.V1;
using CoreSealingPublicKey = Offloadr.Runner.Core.Sealing.SealingPublicKey;
using CoreSealedEnvelope = Offloadr.Runner.Core.Sealing.SealedEnvelope;
using ProtoSealedEnvelope = Offloadr.Runner.V1.SealedEnvelope;
using ProtoSealingPublicKey = Offloadr.Runner.V1.SealingPublicKey;

namespace Offloadr.Runner.Linux;

/// <summary>
/// How one runtime of a session seals its transfers (docs/sealed-envelope.md): the session's
/// reader key, which exists only in memory and is disposed once the runtime's data is removed,
/// the output recipient uploads are sealed to, and whether the sealed-session profile forbids
/// plaintext transfers.
/// </summary>
public sealed class SessionTransferSealing : IDisposable
{
    private readonly SealingKeyPair _readerKey;

    private SessionTransferSealing(
        string sessionId,
        bool memoryWorkspace,
        bool profileRequired,
        CoreSealingPublicKey? outputRecipient,
        SealingKeyPair readerKey)
    {
        SessionId = sessionId;
        MemoryWorkspace = memoryWorkspace;
        ProfileRequired = profileRequired;
        OutputRecipient = outputRecipient;
        _readerKey = readerKey;
        ReaderKeyMessage = new ProtoSealingPublicKey
        {
            KeyId = readerKey.KeyId,
            Algorithm = readerKey.PublicKey.Algorithm,
            PublicKey = Google.Protobuf.ByteString.CopyFrom(readerKey.PublicKey.PublicKey.Span),
        };
    }

    public string SessionId { get; }

    /// <summary>The session's user data lives in memory-backed storage.</summary>
    public bool MemoryWorkspace { get; }

    /// <summary>The sealed-session profile: every upload sealed, every read answered sealed.</summary>
    public bool ProfileRequired { get; }

    /// <summary>Recipient uploads are sealed to; null when uploads travel in plaintext over TLS.</summary>
    internal CoreSealingPublicKey? OutputRecipient { get; }

    internal SealingKeyPair ReaderKey => _readerKey;

    /// <summary>The reader key as sent on every read request.</summary>
    public ProtoSealingPublicKey ReaderKeyMessage { get; }

    public bool SealsUploads => OutputRecipient is not null;

    /// <summary>
    /// Builds the sealing of a new runtime. Throws <see cref="InvalidOperationException"/> when
    /// the start requires the profile but names no usable output recipient: such a session is
    /// refused rather than started with plaintext transfers.
    /// </summary>
    public static SessionTransferSealing Create(string sessionId, SessionSealing? requested, bool memoryWorkspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        CoreSealingPublicKey? recipient = null;
        if (requested?.OutputRecipient is { } key)
        {
            try
            {
                recipient = CoreSealingPublicKey.Create(key.KeyId, key.Algorithm, key.PublicKey.Span);
            }
            catch (SealedEnvelopeException ex)
            {
                throw new InvalidOperationException($"The session's output recipient key is unusable: {ex.Message}", ex);
            }
        }

        var profileRequired = requested?.Required == true;
        if (profileRequired && recipient is null)
        {
            throw new InvalidOperationException("A sealed session needs an output recipient key.");
        }

        if (profileRequired && !memoryWorkspace)
        {
            throw new InvalidOperationException("A sealed session needs a memory-backed workspace.");
        }

        // The private key must never reach a core dump of the agent.
        LinuxProcessHardening.DisableCoreDumps();
        var readerKey = SealingKeyPair.Generate($"reader-{Guid.NewGuid():N}");
        return new SessionTransferSealing(sessionId, memoryWorkspace, profileRequired, recipient, readerKey);
    }

    /// <summary>What the session got, as acknowledged to the control plane.</summary>
    public SessionSealingStatus ToStatus()
        => new()
        {
            MemoryWorkspace = MemoryWorkspace,
            EncryptedUploads = SealsUploads,
            EncryptedReadsRequired = ProfileRequired,
        };

    public void Dispose() => _readerKey.Dispose();

    internal static ProtoSealedEnvelope ToMessage(CoreSealedEnvelope envelope)
        => new()
        {
            Header = Google.Protobuf.ByteString.CopyFrom(envelope.Header.Span),
            EncapsulatedKey = Google.Protobuf.ByteString.CopyFrom(envelope.EncapsulatedKey.Span),
            SealedDataKey = Google.Protobuf.ByteString.CopyFrom(envelope.SealedDataKey.Span),
        };

    internal static CoreSealedEnvelope FromMessage(ProtoSealedEnvelope envelope)
        => new(envelope.Header.Memory, envelope.EncapsulatedKey.Memory, envelope.SealedDataKey.Memory);
}

/// <summary>
/// Writes the payload of a read stream to its destination, opening it first when the metadata
/// carries an envelope. In the sealed-session profile a payload without an envelope is refused
/// before any of it is written. The destination is a temporary publication: callers publish it
/// only after <see cref="CompleteAsync"/> succeeds.
/// </summary>
internal sealed class SealedReadSink : IDisposable
{
    private readonly Stream _destination;
    private readonly SealedEnvelopeReader? _reader;

    private SealedReadSink(Stream destination, SealedEnvelopeReader? reader)
    {
        _destination = destination;
        _reader = reader;
    }

    public bool IsSealed => _reader is not null;

    /// <summary>The plaintext size the envelope's authenticated header declares, when sealed.</summary>
    public long? SealedPlaintextSize => _reader?.Header.PlaintextSize;

    /// <summary>Most wire bytes this payload may have: a sealed payload's exact ciphertext size.</summary>
    public long? SealedCiphertextSize
        => _reader is null ? null : SealedEnvelopeFormat.CiphertextSize(_reader.Header.PlaintextSize);

    /// <summary>
    /// Most bytes a transfer of at most <paramref name="plaintextLimit"/> may put on the wire: a
    /// sealed one carries a 16-byte tag per 64 KiB chunk.
    /// </summary>
    public static long WireLimit(ProtoSealedEnvelope? envelope, long plaintextLimit)
        => envelope is null ? plaintextLimit : SealedEnvelopeFormat.CiphertextSize(plaintextLimit);

    public static SealedReadSink Open(
        Stream destination,
        ProtoSealedEnvelope? envelope,
        SessionTransferSealing? sealing,
        Func<SealedObjectIdentity> expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (envelope is null)
        {
            if (sealing?.ProfileRequired == true)
            {
                throw new InvalidOperationException("A sealed session refuses a transfer that arrived without an envelope.");
            }

            return new SealedReadSink(destination, null);
        }

        if (sealing is null)
        {
            throw new InvalidOperationException("A sealed transfer arrived for a session without a reader key.");
        }

        try
        {
            var reader = SealedEnvelopeReader.Open(
                SessionTransferSealing.FromMessage(envelope),
                sealing.ReaderKey,
                expectedIdentity(),
                destination);
            return new SealedReadSink(destination, reader);
        }
        catch (SealedEnvelopeException ex)
        {
            throw new InvalidOperationException($"A sealed transfer did not open: {ex.Message}", ex);
        }
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> chunk, CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            await _destination.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await _reader.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
        }
        catch (SealedEnvelopeException ex)
        {
            throw new InvalidOperationException($"A sealed transfer failed authentication: {ex.Message}", ex);
        }
    }

    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            await _reader.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SealedEnvelopeException ex)
        {
            throw new InvalidOperationException($"A sealed transfer was incomplete: {ex.Message}", ex);
        }
    }

    public void Dispose() => _reader?.Dispose();
}

/// <summary>
/// Holds the sink a read stream opens once its metadata arrives, so the sink (and the data key
/// it holds) is disposed on every path out of the read.
/// </summary>
internal sealed class SealedReadSinkSlot : IDisposable
{
    public SealedReadSink? Sink { get; private set; }

    /// <summary>The sink chunks are written to; metadata always precedes the first chunk.</summary>
    public SealedReadSink Current
        => Sink ?? throw new InvalidOperationException("A transfer chunk arrived before its metadata.");

    public SealedReadSink Replace(SealedReadSink sink)
    {
        Sink?.Dispose();
        Sink = sink;
        return sink;
    }

    public void Dispose() => Sink?.Dispose();
}

/// <summary>A read-only view of the first <c>length</c> bytes of a stream, for a file that may grow while it is sent.</summary>
internal sealed class LengthLimitedReadStream(Stream inner, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _length - _remaining;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = inner.Read(buffer[..(int)Math.Min(buffer.Length, _remaining)]);
        _remaining -= read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_remaining <= 0)
        {
            return 0;
        }

        var read = await inner.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken).ConfigureAwait(false);
        _remaining -= read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

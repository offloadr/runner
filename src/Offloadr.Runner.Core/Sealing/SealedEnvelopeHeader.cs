using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// The object a sealed payload belongs to. Readers compare the identity in a header with the one
/// they requested, ordinally and field by field, before accepting a payload.
/// </summary>
internal abstract record SealedObjectIdentity
{
    private protected SealedObjectIdentity()
    {
    }

    internal abstract byte Kind { get; }

    internal abstract void Validate();

    internal abstract void Encode(ArrayBufferWriter<byte> output);
}

/// <summary>An editor artifact (input, output or temp file) of a session.</summary>
internal sealed record SealedArtifactIdentity(string SessionId, string EditorSid, string Type, string Subfolder, string Filename)
    : SealedObjectIdentity
{
    internal override byte Kind => 0x01;

    internal override void Validate()
    {
        SealedEnvelopeHeader.ValidateField(SessionId, SealedEnvelopeHeader.MaxSessionIdBytes, nameof(SessionId));
        SealedEnvelopeHeader.ValidateField(EditorSid, SealedEnvelopeHeader.MaxEditorSidBytes, nameof(EditorSid));
        SealedEnvelopeHeader.ValidateField(Type, SealedEnvelopeHeader.MaxTypeBytes, nameof(Type));
        SealedEnvelopeHeader.ValidateField(Subfolder, SealedEnvelopeHeader.MaxSubfolderBytes, nameof(Subfolder), allowEmpty: true);
        SealedEnvelopeHeader.ValidateField(Filename, SealedEnvelopeHeader.MaxFilenameBytes, nameof(Filename));
    }

    internal override void Encode(ArrayBufferWriter<byte> output)
    {
        SealedEnvelopeHeader.WriteString(output, SessionId);
        SealedEnvelopeHeader.WriteString(output, EditorSid);
        SealedEnvelopeHeader.WriteString(output, Type);
        SealedEnvelopeHeader.WriteString(output, Subfolder);
        SealedEnvelopeHeader.WriteString(output, Filename);
    }
}

/// <summary>One mirrored workspace file. <paramref name="Root"/> is the workspace root name, such as "user".</summary>
internal sealed record SealedWorkspaceFileIdentity(string SessionId, string Root, string RelativePath)
    : SealedObjectIdentity
{
    internal override byte Kind => 0x02;

    internal override void Validate()
    {
        SealedEnvelopeHeader.ValidateField(SessionId, SealedEnvelopeHeader.MaxSessionIdBytes, nameof(SessionId));
        SealedEnvelopeHeader.ValidateField(Root, SealedEnvelopeHeader.MaxRootBytes, nameof(Root));
        SealedEnvelopeHeader.ValidateField(RelativePath, SealedEnvelopeHeader.MaxRelativePathBytes, nameof(RelativePath));
    }

    internal override void Encode(ArrayBufferWriter<byte> output)
    {
        SealedEnvelopeHeader.WriteString(output, SessionId);
        SealedEnvelopeHeader.WriteString(output, Root);
        SealedEnvelopeHeader.WriteString(output, RelativePath);
    }
}

/// <summary>The seed archive of one workspace root.</summary>
internal sealed record SealedSeedArchiveIdentity(string SessionId, string Root)
    : SealedObjectIdentity
{
    internal override byte Kind => 0x03;

    internal override void Validate()
    {
        SealedEnvelopeHeader.ValidateField(SessionId, SealedEnvelopeHeader.MaxSessionIdBytes, nameof(SessionId));
        SealedEnvelopeHeader.ValidateField(Root, SealedEnvelopeHeader.MaxRootBytes, nameof(Root));
    }

    internal override void Encode(ArrayBufferWriter<byte> output)
    {
        SealedEnvelopeHeader.WriteString(output, SessionId);
        SealedEnvelopeHeader.WriteString(output, Root);
    }
}

/// <summary>
/// The canonical sealed-envelope header. Its encoding is deterministic and is the AAD that seals
/// the data key, so any change to a field makes the envelope fail to open.
/// </summary>
internal sealed record SealedEnvelopeHeader(string KeyId, long PlaintextSize, SealedObjectIdentity Identity)
{
    public const int MaxEncodedSize = 8192;
    public const int MaxKeyIdBytes = 128;
    public const int MaxSessionIdBytes = 128;
    public const int MaxEditorSidBytes = 128;
    public const int MaxTypeBytes = 64;
    public const int MaxSubfolderBytes = 1024;
    public const int MaxFilenameBytes = 255;
    public const int MaxRootBytes = 64;
    public const int MaxRelativePathBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>"OFLDSEAL".</summary>
    public static ReadOnlySpan<byte> Magic => "OFLDSEAL"u8;

    public byte[] Encode()
    {
        ArgumentNullException.ThrowIfNull(KeyId);
        ArgumentNullException.ThrowIfNull(Identity);
        ValidateKeyId(KeyId);
        SealedEnvelopeFormat.ValidatePlaintextSize(PlaintextSize);
        Identity.Validate();

        var output = new ArrayBufferWriter<byte>(256);
        output.Write(Magic);
        WriteUInt16(output, SealedEnvelopeFormat.Version);
        WriteUInt16(output, Hpke.KemId);
        WriteUInt16(output, Hpke.KdfId);
        WriteUInt16(output, Hpke.AeadId);
        WriteUInt16(output, SealedEnvelopeFormat.PayloadId);
        var chunkSize = output.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(chunkSize, SealedEnvelopeFormat.ChunkSize);
        output.Advance(sizeof(uint));
        var plaintextSize = output.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64BigEndian(plaintextSize, (ulong)PlaintextSize);
        output.Advance(sizeof(ulong));
        WriteString(output, KeyId);
        output.Write([Identity.Kind]);
        Identity.Encode(output);
        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Parses a header, rejecting anything that is not exactly the canonical encoding of a
    /// version 1 header for this suite. Throws <see cref="SealedEnvelopeException"/>.
    /// </summary>
    public static SealedEnvelopeHeader Parse(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > MaxEncodedSize)
        {
            throw new SealedEnvelopeException("Sealed header is too large.");
        }

        var reader = new HeaderReader(encoded);
        if (!reader.ReadBytes(Magic.Length).SequenceEqual(Magic))
        {
            throw new SealedEnvelopeException("Not a sealed envelope header.");
        }

        if (reader.ReadUInt16() != SealedEnvelopeFormat.Version)
        {
            throw new SealedEnvelopeException("Unsupported sealed envelope version.");
        }

        if (reader.ReadUInt16() != Hpke.KemId
            || reader.ReadUInt16() != Hpke.KdfId
            || reader.ReadUInt16() != Hpke.AeadId
            || reader.ReadUInt16() != SealedEnvelopeFormat.PayloadId)
        {
            throw new SealedEnvelopeException("Unsupported sealed envelope suite.");
        }

        if (reader.ReadUInt32() != SealedEnvelopeFormat.ChunkSize)
        {
            throw new SealedEnvelopeException("Unsupported sealed payload chunk size.");
        }

        var plaintextSize = reader.ReadUInt64();
        if (plaintextSize > SealedEnvelopeFormat.MaxPlaintextSize)
        {
            throw new SealedEnvelopeException("Sealed payload is larger than the format allows.");
        }

        var keyId = reader.ReadString(MaxKeyIdBytes);
        SealedObjectIdentity identity = reader.ReadByte() switch
        {
            0x01 => new SealedArtifactIdentity(
                reader.ReadString(MaxSessionIdBytes),
                reader.ReadString(MaxEditorSidBytes),
                reader.ReadString(MaxTypeBytes),
                reader.ReadString(MaxSubfolderBytes),
                reader.ReadString(MaxFilenameBytes)),
            0x02 => new SealedWorkspaceFileIdentity(
                reader.ReadString(MaxSessionIdBytes),
                reader.ReadString(MaxRootBytes),
                reader.ReadString(MaxRelativePathBytes)),
            0x03 => new SealedSeedArchiveIdentity(
                reader.ReadString(MaxSessionIdBytes),
                reader.ReadString(MaxRootBytes)),
            _ => throw new SealedEnvelopeException("Unknown sealed object identity kind."),
        };

        if (!reader.AtEnd)
        {
            throw new SealedEnvelopeException("Sealed header has trailing bytes.");
        }

        var header = new SealedEnvelopeHeader(keyId, (long)plaintextSize, identity);
        ValidateKeyId(keyId);
        identity.Validate();
        return header;
    }

    internal static void ValidateKeyId(string keyId) => ValidateField(keyId, MaxKeyIdBytes, "KeyId");

    internal static void ValidateField(string value, int maxBytes, string name, bool allowEmpty = false)
    {
        if (value is null)
        {
            throw new SealedEnvelopeException($"Sealed header field {name} is missing.");
        }

        if (value.Length == 0 && !allowEmpty)
        {
            throw new SealedEnvelopeException($"Sealed header field {name} is empty.");
        }

        int byteCount;
        try
        {
            byteCount = StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException ex)
        {
            throw new SealedEnvelopeException($"Sealed header field {name} is not valid Unicode.", ex);
        }

        if (byteCount > maxBytes)
        {
            throw new SealedEnvelopeException($"Sealed header field {name} is longer than {maxBytes} bytes.");
        }
    }

    internal static void WriteString(ArrayBufferWriter<byte> output, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteUInt16(output, checked((ushort)bytes.Length));
        output.Write(bytes);
    }

    private static void WriteUInt16(ArrayBufferWriter<byte> output, ushort value)
    {
        BinaryPrimitives.WriteUInt16BigEndian(output.GetSpan(sizeof(ushort)), value);
        output.Advance(sizeof(ushort));
    }

    private ref struct HeaderReader(ReadOnlySpan<byte> data)
    {
        private ReadOnlySpan<byte> remaining = data;

        public readonly bool AtEnd => remaining.IsEmpty;

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            if (remaining.Length < count)
            {
                throw new SealedEnvelopeException("Sealed header is truncated.");
            }

            var value = remaining[..count];
            remaining = remaining[count..];
            return value;
        }

        public byte ReadByte() => ReadBytes(1)[0];

        public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(sizeof(ushort)));

        public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(sizeof(uint)));

        public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(sizeof(ulong)));

        public string ReadString(int maxBytes)
        {
            var length = ReadUInt16();
            if (length > maxBytes)
            {
                throw new SealedEnvelopeException($"Sealed header string is longer than {maxBytes} bytes.");
            }

            try
            {
                return StrictUtf8.GetString(ReadBytes(length));
            }
            catch (DecoderFallbackException ex)
            {
                throw new SealedEnvelopeException("Sealed header string is not valid UTF-8.", ex);
            }
        }
    }
}

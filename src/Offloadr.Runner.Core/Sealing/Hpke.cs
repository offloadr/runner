using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// HPKE (RFC 9180) base mode for exactly one suite: DHKEM(P-256, HKDF-SHA256), HKDF-SHA256 and
/// AES-128-GCM. Sealed envelopes only use single-shot <see cref="Seal"/> and <see cref="Open"/>;
/// the context and KEM steps are exposed internally so the RFC test vectors can check each stage.
/// </summary>
internal static class Hpke
{
    public const ushort KemId = 0x0010;
    public const ushort KdfId = 0x0001;
    public const ushort AeadId = 0x0001;

    public const int EncapsulatedKeySize = P256.PublicKeySize; // Nenc
    public const int KeySize = 16; // Nk
    public const int NonceSize = 12; // Nn
    public const int TagSize = 16; // Nt
    public const int HashSize = 32; // Nh, also the KEM's Nsecret

    private const byte ModeBase = 0x00;

    private static ReadOnlySpan<byte> VersionLabel => "HPKE-v1"u8;

    // "KEM" || I2OSP(kem_id, 2)
    private static ReadOnlySpan<byte> KemSuiteId => [0x4B, 0x45, 0x4D, 0x00, 0x10];

    // "HPKE" || I2OSP(kem_id, 2) || I2OSP(kdf_id, 2) || I2OSP(aead_id, 2)
    private static ReadOnlySpan<byte> HpkeSuiteId => [0x48, 0x50, 0x4B, 0x45, 0x00, 0x10, 0x00, 0x01, 0x00, 0x01];

    /// <summary>Single-shot base-mode seal to <paramref name="recipientPublicKey"/> with a fresh ephemeral key.</summary>
    public static byte[] Seal(
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> info,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> plaintext,
        out byte[] encapsulatedKey)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var context = SetupBaseS(recipientPublicKey, info, ephemeral, out encapsulatedKey);
        return context.Seal(0, aad, plaintext);
    }

    /// <summary>Single-shot base-mode open. Throws <see cref="CryptographicException"/> on any failure.</summary>
    public static byte[] Open(
        ECDiffieHellman recipientPrivateKey,
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> encapsulatedKey,
        ReadOnlySpan<byte> info,
        ReadOnlySpan<byte> aad,
        ReadOnlySpan<byte> ciphertext)
    {
        using var context = SetupBaseR(encapsulatedKey, recipientPrivateKey, recipientPublicKey, info);
        return context.Open(0, aad, ciphertext);
    }

    internal static HpkeContext SetupBaseS(
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> info,
        ECDiffieHellman ephemeral,
        out byte[] encapsulatedKey)
    {
        var sharedSecret = Encap(recipientPublicKey, ephemeral, out encapsulatedKey);
        try
        {
            return KeySchedule(sharedSecret, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    internal static HpkeContext SetupBaseR(
        ReadOnlySpan<byte> encapsulatedKey,
        ECDiffieHellman recipientPrivateKey,
        ReadOnlySpan<byte> recipientPublicKey,
        ReadOnlySpan<byte> info)
    {
        var sharedSecret = Decap(encapsulatedKey, recipientPrivateKey, recipientPublicKey);
        try
        {
            return KeySchedule(sharedSecret, info);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
        }
    }

    internal static byte[] Encap(ReadOnlySpan<byte> recipientPublicKey, ECDiffieHellman ephemeral, out byte[] encapsulatedKey)
    {
        encapsulatedKey = P256.ExportPublicKey(ephemeral);
        var dh = P256.DiffieHellman(ephemeral, recipientPublicKey);
        try
        {
            return ExtractAndExpand(dh, encapsulatedKey, recipientPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dh);
        }
    }

    internal static byte[] Decap(ReadOnlySpan<byte> encapsulatedKey, ECDiffieHellman recipientPrivateKey, ReadOnlySpan<byte> recipientPublicKey)
    {
        var dh = P256.DiffieHellman(recipientPrivateKey, encapsulatedKey);
        try
        {
            return ExtractAndExpand(dh, encapsulatedKey, recipientPublicKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dh);
        }
    }

    /// <summary>DHKEM ExtractAndExpand with kem_context = enc || pkRm.</summary>
    private static byte[] ExtractAndExpand(ReadOnlySpan<byte> dh, ReadOnlySpan<byte> encapsulatedKey, ReadOnlySpan<byte> recipientPublicKey)
    {
        Span<byte> kemContext = stackalloc byte[encapsulatedKey.Length + recipientPublicKey.Length];
        encapsulatedKey.CopyTo(kemContext);
        recipientPublicKey.CopyTo(kemContext[encapsulatedKey.Length..]);

        Span<byte> eaePrk = stackalloc byte[HashSize];
        var sharedSecret = new byte[HashSize];
        try
        {
            LabeledExtract(KemSuiteId, [], "eae_prk"u8, dh, eaePrk);
            LabeledExpand(KemSuiteId, eaePrk, "shared_secret"u8, kemContext, sharedSecret);
            return sharedSecret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(eaePrk);
        }
    }

    private static HpkeContext KeySchedule(ReadOnlySpan<byte> sharedSecret, ReadOnlySpan<byte> info)
    {
        // Base mode: psk and psk_id are empty.
        Span<byte> keyScheduleContext = stackalloc byte[1 + (2 * HashSize)];
        keyScheduleContext[0] = ModeBase;
        LabeledExtract(HpkeSuiteId, [], "psk_id_hash"u8, [], keyScheduleContext.Slice(1, HashSize));
        LabeledExtract(HpkeSuiteId, [], "info_hash"u8, info, keyScheduleContext.Slice(1 + HashSize, HashSize));

        Span<byte> secret = stackalloc byte[HashSize];
        var key = new byte[KeySize];
        var baseNonce = new byte[NonceSize];
        var exporterSecret = new byte[HashSize];
        try
        {
            LabeledExtract(HpkeSuiteId, sharedSecret, "secret"u8, [], secret);
            LabeledExpand(HpkeSuiteId, secret, "key"u8, keyScheduleContext, key);
            LabeledExpand(HpkeSuiteId, secret, "base_nonce"u8, keyScheduleContext, baseNonce);
            LabeledExpand(HpkeSuiteId, secret, "exp"u8, keyScheduleContext, exporterSecret);
            return new HpkeContext(key, baseNonce, exporterSecret);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>LabeledExtract(salt, label, ikm) = Extract(salt, "HPKE-v1" || suite_id || label || ikm).</summary>
    internal static void LabeledExtract(
        ReadOnlySpan<byte> suiteId,
        ReadOnlySpan<byte> salt,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> ikm,
        Span<byte> prk)
    {
        var labeledIkm = new byte[VersionLabel.Length + suiteId.Length + label.Length + ikm.Length];
        try
        {
            var span = labeledIkm.AsSpan();
            VersionLabel.CopyTo(span);
            span = span[VersionLabel.Length..];
            suiteId.CopyTo(span);
            span = span[suiteId.Length..];
            label.CopyTo(span);
            ikm.CopyTo(span[label.Length..]);
            HKDF.Extract(HashAlgorithmName.SHA256, labeledIkm, salt, prk);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(labeledIkm);
        }
    }

    /// <summary>LabeledExpand(prk, label, info, L) = Expand(prk, I2OSP(L, 2) || "HPKE-v1" || suite_id || label || info, L).</summary>
    internal static void LabeledExpand(
        ReadOnlySpan<byte> suiteId,
        ReadOnlySpan<byte> prk,
        ReadOnlySpan<byte> label,
        ReadOnlySpan<byte> info,
        Span<byte> output)
    {
        var labeledInfo = new byte[2 + VersionLabel.Length + suiteId.Length + label.Length + info.Length];
        var span = labeledInfo.AsSpan();
        BinaryPrimitives.WriteUInt16BigEndian(span, checked((ushort)output.Length));
        span = span[2..];
        VersionLabel.CopyTo(span);
        span = span[VersionLabel.Length..];
        suiteId.CopyTo(span);
        span = span[suiteId.Length..];
        label.CopyTo(span);
        info.CopyTo(span[label.Length..]);
        HKDF.Expand(HashAlgorithmName.SHA256, prk, output, labeledInfo);
    }

    internal static void Export(ReadOnlySpan<byte> exporterSecret, ReadOnlySpan<byte> exporterContext, Span<byte> output)
        => LabeledExpand(HpkeSuiteId, exporterSecret, "sec"u8, exporterContext, output);
}

/// <summary>An HPKE encryption context. Callers choose the sequence number; never reuse one with the same context.</summary>
internal sealed class HpkeContext : IDisposable
{
    private readonly byte[] key;
    private readonly byte[] baseNonce;
    private readonly byte[] exporterSecret;
    private bool disposed;

    internal HpkeContext(byte[] key, byte[] baseNonce, byte[] exporterSecret)
    {
        this.key = key;
        this.baseNonce = baseNonce;
        this.exporterSecret = exporterSecret;
    }

    internal ReadOnlySpan<byte> Key => Live(key);

    internal ReadOnlySpan<byte> BaseNonce => Live(baseNonce);

    internal ReadOnlySpan<byte> ExporterSecret => Live(exporterSecret);

    public byte[] Seal(ulong sequenceNumber, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> plaintext)
    {
        Span<byte> nonce = stackalloc byte[Hpke.NonceSize];
        ComputeNonce(sequenceNumber, nonce);
        var ciphertext = new byte[plaintext.Length + Hpke.TagSize];
        using var aes = new AesGcm(Live(key), Hpke.TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), aad);
        return ciphertext;
    }

    public byte[] Open(ulong sequenceNumber, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < Hpke.TagSize)
        {
            throw new CryptographicException("HPKE ciphertext is shorter than its tag.");
        }

        Span<byte> nonce = stackalloc byte[Hpke.NonceSize];
        ComputeNonce(sequenceNumber, nonce);
        var plaintextLength = ciphertext.Length - Hpke.TagSize;
        var plaintext = new byte[plaintextLength];
        using var aes = new AesGcm(Live(key), Hpke.TagSize);
        aes.Decrypt(nonce, ciphertext[..plaintextLength], ciphertext[plaintextLength..], plaintext, aad);
        return plaintext;
    }

    public void Export(ReadOnlySpan<byte> exporterContext, Span<byte> output)
        => Hpke.Export(Live(exporterSecret), exporterContext, output);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(baseNonce);
        CryptographicOperations.ZeroMemory(exporterSecret);
    }

    /// <summary>nonce = base_nonce XOR I2OSP(seq, Nn).</summary>
    private void ComputeNonce(ulong sequenceNumber, Span<byte> nonce)
    {
        Live(baseNonce).CopyTo(nonce);
        Span<byte> sequence = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(sequence, sequenceNumber);
        var tail = nonce[(Hpke.NonceSize - sizeof(ulong))..];
        for (var i = 0; i < sizeof(ulong); i++)
        {
            tail[i] ^= sequence[i];
        }
    }

    private ReadOnlySpan<byte> Live(byte[] value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return value;
    }
}

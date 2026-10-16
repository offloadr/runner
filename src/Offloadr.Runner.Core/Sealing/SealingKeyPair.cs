using System.Security.Cryptography;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// A P-256 sealing key pair held only in memory, such as a session's reader key. The private key
/// never leaves the process; disposing releases the native key, which the platform clears.
/// </summary>
internal sealed class SealingKeyPair : IDisposable
{
    private readonly ECDiffieHellman key;
    private bool disposed;

    private SealingKeyPair(string keyId, ECDiffieHellman key)
    {
        this.key = key;
        PublicKey = SealingPublicKey.Create(keyId, SealedEnvelopeFormat.AlgorithmId, P256.ExportPublicKey(key));
    }

    public SealingPublicKey PublicKey { get; }

    public string KeyId => PublicKey.KeyId;

    internal ECDiffieHellman Key
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return key;
        }
    }

    public static SealingKeyPair Generate(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        SealedEnvelopeHeader.ValidateKeyId(keyId);
        var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            return new SealingKeyPair(keyId, key);
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Imports a known key pair; for test vectors and interop checks only.</summary>
    internal static SealingKeyPair Import(string keyId, ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        SealedEnvelopeHeader.ValidateKeyId(keyId);
        var key = P256.ImportPrivateKey(privateKey, publicKey);
        try
        {
            var pair = new SealingKeyPair(keyId, key);
            if (!CryptographicOperations.FixedTimeEquals(pair.PublicKey.PublicKey.Span, publicKey))
            {
                throw new SealedEnvelopeException("Public key does not belong to the private key.");
            }

            return pair;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>The uncompressed SEC1 public key (65 bytes).</summary>
    public byte[] ExportPublicKey() => PublicKey.PublicKey.ToArray();

    /// <summary>The 32-byte private scalar; for interop checks only. Callers zero the result.</summary>
    internal byte[] ExportPrivateKey()
    {
        var parameters = Key.ExportParameters(includePrivateParameters: true);
        try
        {
            var d = parameters.D ?? throw new CryptographicException("Key has no private scalar.");
            var scalar = new byte[P256.PrivateKeySize];
            d.CopyTo(scalar.AsSpan(P256.PrivateKeySize - d.Length));
            return scalar;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(parameters.D);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        key.Dispose();
    }
}

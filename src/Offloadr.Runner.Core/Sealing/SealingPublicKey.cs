namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// A validated sealing recipient: key id, algorithm id and a 65-byte uncompressed P-256 point,
/// as carried by the SealingPublicKey contract message.
/// </summary>
internal sealed class SealingPublicKey
{
    private readonly byte[] publicKey;

    private SealingPublicKey(string keyId, byte[] publicKey)
    {
        KeyId = keyId;
        this.publicKey = publicKey;
    }

    public string KeyId { get; }

    public string Algorithm => SealedEnvelopeFormat.AlgorithmId;

    /// <summary>The uncompressed SEC1 point (0x04 || X || Y).</summary>
    public ReadOnlyMemory<byte> PublicKey => publicKey;

    /// <summary>
    /// Validates a recipient received from elsewhere. Throws <see cref="SealedEnvelopeException"/>
    /// for an unknown algorithm, an invalid key id or a public key that is not on P-256.
    /// </summary>
    public static SealingPublicKey Create(string keyId, string algorithm, ReadOnlySpan<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(algorithm);
        if (!string.Equals(algorithm, SealedEnvelopeFormat.AlgorithmId, StringComparison.Ordinal))
        {
            throw new SealedEnvelopeException("Unsupported sealing algorithm.");
        }

        SealedEnvelopeHeader.ValidateKeyId(keyId);
        P256.ValidatePublicKey(publicKey);
        return new SealingPublicKey(keyId, publicKey.ToArray());
    }
}

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace Offloadr.Runner.Core.Sealing;

/// <summary>
/// P-256 key handling for sealed envelopes. Public keys are 65-byte uncompressed SEC1 points
/// (<c>0x04 || X || Y</c>) and are checked against the curve equation here, so acceptance does
/// not depend on what the platform's crypto library validates on import.
/// </summary>
internal static class P256
{
    public const int PublicKeySize = 65;
    public const int PrivateKeySize = 32;
    private const int CoordinateSize = 32;

    private static readonly BigInteger Prime = BigInteger.Parse(
        "0ffffffff00000001000000000000000000000000ffffffffffffffffffffffff",
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    private static readonly BigInteger B = BigInteger.Parse(
        "05ac635d8aa3a93e7b3ebbd55769886bc651d06b0cc53b0f63bce3c3e27d2604b",
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture);

    /// <summary>Throws <see cref="SealedEnvelopeException"/> unless the bytes are an uncompressed point on P-256.</summary>
    public static void ValidatePublicKey(ReadOnlySpan<byte> publicKey)
    {
        if (publicKey.Length != PublicKeySize || publicKey[0] != 0x04)
        {
            throw new SealedEnvelopeException("Public key is not a 65-byte uncompressed P-256 point.");
        }

        var x = new BigInteger(publicKey.Slice(1, CoordinateSize), isUnsigned: true, isBigEndian: true);
        var y = new BigInteger(publicKey.Slice(1 + CoordinateSize, CoordinateSize), isUnsigned: true, isBigEndian: true);
        if (x >= Prime || y >= Prime)
        {
            throw new SealedEnvelopeException("Public key coordinates are out of range for P-256.");
        }

        var left = y * y % Prime;
        var right = ((x * x * x) - (3 * x) + B) % Prime;
        if (right.Sign < 0)
        {
            right += Prime;
        }

        if (left != right)
        {
            throw new SealedEnvelopeException("Public key is not a point on P-256.");
        }
    }

    public static ECDiffieHellman ImportPublicKey(ReadOnlySpan<byte> publicKey)
    {
        ValidatePublicKey(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = publicKey.Slice(1, CoordinateSize).ToArray(),
                Y = publicKey.Slice(1 + CoordinateSize, CoordinateSize).ToArray(),
            },
        });
    }

    public static ECDiffieHellman ImportPrivateKey(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> publicKey)
    {
        if (privateKey.Length != PrivateKeySize)
        {
            throw new SealedEnvelopeException("Private key is not a 32-byte P-256 scalar.");
        }

        ValidatePublicKey(publicKey);
        var d = privateKey.ToArray();
        try
        {
            return ECDiffieHellman.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                D = d,
                Q = new ECPoint
                {
                    X = publicKey.Slice(1, CoordinateSize).ToArray(),
                    Y = publicKey.Slice(1 + CoordinateSize, CoordinateSize).ToArray(),
                },
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(d);
        }
    }

    public static byte[] ExportPublicKey(ECDiffieHellman key)
    {
        var q = key.ExportParameters(includePrivateParameters: false).Q;
        var x = q.X ?? throw new CryptographicException("Key has no public point.");
        var y = q.Y ?? throw new CryptographicException("Key has no public point.");
        var encoded = new byte[PublicKeySize];
        encoded[0] = 0x04;
        x.CopyTo(encoded.AsSpan(1 + CoordinateSize - x.Length));
        y.CopyTo(encoded.AsSpan(1 + (2 * CoordinateSize) - y.Length));
        return encoded;
    }

    /// <summary>The ECDH shared secret: the 32-byte X coordinate of the shared point.</summary>
    public static byte[] DiffieHellman(ECDiffieHellman privateKey, ReadOnlySpan<byte> peerPublicKey)
    {
        using var peer = ImportPublicKey(peerPublicKey);
        using var peerPublic = peer.PublicKey;
        return privateKey.DeriveRawSecretAgreement(peerPublic);
    }
}

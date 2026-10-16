using Offloadr.Runner.Core.Sealing;

namespace Offloadr.Runner.Tests;

/// <summary>RFC 9180 Appendix A.3.1: DHKEM(P-256, HKDF-SHA256), HKDF-SHA256, AES-128-GCM, base mode.</summary>
public class HpkeTests
{
    private const string Info = "4f6465206f6e2061204772656369616e2055726e";
    private const string SkEm = "4995788ef4b9d6132b249ce59a77281493eb39af373d236a1fe415cb0c2d7beb";
    private const string SkRm = "f3ce7fdae57e1a310d87f1ebbde6f328be0a99cdbcadf4d6589cf29de4b8ffd2";
    private const string SharedSecret = "c0d26aeab536609a572b07695d933b589dcf363ff9d93c93adea537aeabb8cb8";
    private const string Key = "868c066ef58aae6dc589b6cfdd18f97e";
    private const string BaseNonce = "4e0bc5018beba4bf004cca59";
    private const string ExporterSecret = "14ad94af484a7ad3ef40e9f3be99ecc6fa9036df9d4920548424df127ee0d99f";
    private const string Plaintext = "4265617574792069732074727574682c20747275746820626561757479";

    private const string PkEm =
        "04a92719c6195d5085104f469a8b9814d5838ff72b60501e2c4466e5e67b32"
        + "5ac98536d7b61a1af4b78e5b7f951c0900be863c403ce65c9bfcb9382657222d18c4";

    private const string PkRm =
        "04fe8c19ce0905191ebc298a9245792531f26f0cece2460639e8bc39cb7f70"
        + "6a826a779b4cf969b8a0e539c7f62fb3d30ad6aa8f80e30f1d128aafd68a2ce72ea0";

    [Test]
    public void ImportedKeys_ExportTheirRfcPublicKeys()
    {
        using var ephemeral = P256.ImportPrivateKey(Hex(SkEm), Hex(PkEm));
        using var recipient = P256.ImportPrivateKey(Hex(SkRm), Hex(PkRm));

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexStringLower(P256.ExportPublicKey(ephemeral)), Is.EqualTo(PkEm));
            Assert.That(Convert.ToHexStringLower(P256.ExportPublicKey(recipient)), Is.EqualTo(PkRm));
        });
    }

    [Test]
    public void EncapAndDecap_ProduceTheRfcSharedSecret()
    {
        using var ephemeral = P256.ImportPrivateKey(Hex(SkEm), Hex(PkEm));
        using var recipient = P256.ImportPrivateKey(Hex(SkRm), Hex(PkRm));

        var sent = Hpke.Encap(Hex(PkRm), ephemeral, out var encapsulatedKey);
        var received = Hpke.Decap(encapsulatedKey, recipient, Hex(PkRm));

        Assert.Multiple(() =>
        {
            // enc is the serialized ephemeral public key, so it equals pkEm.
            Assert.That(Convert.ToHexStringLower(encapsulatedKey), Is.EqualTo(PkEm));
            Assert.That(Convert.ToHexStringLower(sent), Is.EqualTo(SharedSecret));
            Assert.That(Convert.ToHexStringLower(received), Is.EqualTo(SharedSecret));
        });
    }

    [Test]
    public void KeySchedule_ProducesTheRfcKeyNonceAndExporterSecret()
    {
        using var ephemeral = P256.ImportPrivateKey(Hex(SkEm), Hex(PkEm));
        using var recipient = P256.ImportPrivateKey(Hex(SkRm), Hex(PkRm));

        using var sender = Hpke.SetupBaseS(Hex(PkRm), Hex(Info), ephemeral, out var encapsulatedKey);
        using var receiver = Hpke.SetupBaseR(encapsulatedKey, recipient, Hex(PkRm), Hex(Info));

        Assert.Multiple(() =>
        {
            foreach (var context in new[] { sender, receiver })
            {
                Assert.That(Convert.ToHexStringLower(context.Key), Is.EqualTo(Key));
                Assert.That(Convert.ToHexStringLower(context.BaseNonce), Is.EqualTo(BaseNonce));
                Assert.That(Convert.ToHexStringLower(context.ExporterSecret), Is.EqualTo(ExporterSecret));
            }
        });
    }

    [TestCase(0UL, "436f756e742d30", "5ad590bb8baa577f8619db35a36311226a896e7342a6d836d8b7bcd2f20b6c7f9076ac232e3ab2523f39513434")]
    [TestCase(1UL, "436f756e742d31", "fa6f037b47fc21826b610172ca9637e82d6e5801eb31cbd3748271affd4ecb06646e0329cbdf3c3cd655b28e82")]
    [TestCase(2UL, "436f756e742d32", "895cabfac50ce6c6eb02ffe6c048bf53b7f7be9a91fc559402cbc5b8dcaeb52b2ccc93e466c28fb55fed7a7fec")]
    [TestCase(4UL, "436f756e742d34", "8787491ee8df99bc99a246c4b3216d3d57ab5076e18fa27133f520703bc70ec999dd36ce042e44f0c3169a6a8f")]
    [TestCase(255UL, "436f756e742d323535", "2ad71c85bf3f45c6eca301426289854b31448bcf8a8ccb1deef3ebd87f60848aa53c538c30a4dac71d619ee2cd")]
    [TestCase(256UL, "436f756e742d323536", "10f179686aa2caec1758c8e554513f16472bd0a11e2a907dde0b212cbe87d74f367f8ffe5e41cd3e9962a6afb2")]
    public void Encryptions_MatchTheRfcCiphertexts(ulong sequenceNumber, string aad, string ciphertext)
    {
        using var ephemeral = P256.ImportPrivateKey(Hex(SkEm), Hex(PkEm));
        using var recipient = P256.ImportPrivateKey(Hex(SkRm), Hex(PkRm));
        using var sender = Hpke.SetupBaseS(Hex(PkRm), Hex(Info), ephemeral, out var encapsulatedKey);
        using var receiver = Hpke.SetupBaseR(encapsulatedKey, recipient, Hex(PkRm), Hex(Info));

        var sealedBytes = sender.Seal(sequenceNumber, Hex(aad), Hex(Plaintext));
        var opened = receiver.Open(sequenceNumber, Hex(aad), Hex(ciphertext));

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexStringLower(sealedBytes), Is.EqualTo(ciphertext));
            Assert.That(Convert.ToHexStringLower(opened), Is.EqualTo(Plaintext));
        });
    }

    [TestCase("", "5e9bc3d236e1911d95e65b576a8a86d478fb827e8bdfe77b741b289890490d4d")]
    [TestCase("00", "6cff87658931bda83dc857e6353efe4987a201b849658d9b047aab4cf216e796")]
    [TestCase("54657374436f6e74657874", "d8f1ea7942adbba7412c6d431c62d01371ea476b823eb697e1f6e6cae1dab85a")]
    public void Exports_MatchTheRfcValues(string exporterContext, string exportedValue)
    {
        using var ephemeral = P256.ImportPrivateKey(Hex(SkEm), Hex(PkEm));
        using var sender = Hpke.SetupBaseS(Hex(PkRm), Hex(Info), ephemeral, out _);
        var exported = new byte[32];

        sender.Export(Hex(exporterContext), exported);

        Assert.That(Convert.ToHexStringLower(exported), Is.EqualTo(exportedValue));
    }

    [Test]
    public void SingleShot_RoundTripsAndRejectsChangedAad()
    {
        using var recipient = SealingKeyPair.Generate("recipient");
        var publicKey = recipient.ExportPublicKey();

        var ciphertext = Hpke.Seal(publicKey, Hex(Info), "aad"u8, Hex(Plaintext), out var encapsulatedKey);
        var opened = Hpke.Open(recipient.Key, publicKey, encapsulatedKey, Hex(Info), "aad"u8, ciphertext);

        Assert.Multiple(() =>
        {
            Assert.That(Convert.ToHexStringLower(opened), Is.EqualTo(Plaintext));
            Assert.That(
                () => Hpke.Open(recipient.Key, publicKey, encapsulatedKey, Hex(Info), "other"u8, ciphertext),
                Throws.InstanceOf<System.Security.Cryptography.CryptographicException>());
        });
    }

    private static byte[] Hex(string value) => Convert.FromHexString(value);
}

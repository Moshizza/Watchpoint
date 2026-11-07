using System.Text;
using Shouldly;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Secrets.Internal;
using Xunit;

namespace Watchpoint.Secrets.Tests;

[Trait("layer", "U")]
[Trait("spec", "SEC")]
public sealed class EnvelopeAndValueTests
{
    private static readonly Guid _id = Guid.CreateVersion7();
    private readonly MasterKey _key = MasterKey.Generate();

    [Fact]
    public void SEC_U_001_Envelope_round_trip()
    {
        var encrypted = EnvelopeCipher.Encrypt(_id, 1, "p@ss"u8, _key);

        using var value = EnvelopeCipher.Decrypt(_id, 1, encrypted, new MasterKeyRing(_key)).Value;

        value.Use(v => v.ToArray()).ShouldBe("p@ss"u8.ToArray());
        encrypted.KeyId.ShouldBe(_key.KeyId);
        encrypted.Ciphertext.Length.ShouldBe(4 + EnvelopeCipher.TagSize);
    }

    [Fact]
    public void SEC_U_002_Nonces_unique()
    {
        var nonces = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 10_000; i++)
        {
            var encrypted = EnvelopeCipher.Encrypt(_id, 1, "x"u8, _key);
            nonces.Add(Convert.ToBase64String(encrypted.Nonce)).ShouldBeTrue();
            nonces.Add(Convert.ToBase64String(encrypted.DekNonce)).ShouldBeTrue();
        }
    }

    [Fact]
    public void SEC_U_003_Tampered_ciphertext_detected()
    {
        var encrypted = EnvelopeCipher.Encrypt(_id, 1, "p@ss"u8, _key);
        encrypted.Ciphertext[1] ^= 0x01;

        EnvelopeCipher.Decrypt(_id, 1, encrypted, new MasterKeyRing(_key)).Error.Code.Value.ShouldBe(SecretsErrors.IntegrityFailedCode);
    }

    [Fact]
    public void SEC_U_004_AAD_binds_id_and_version()
    {
        var encrypted = EnvelopeCipher.Encrypt(_id, 1, "p@ss"u8, _key);

        EnvelopeCipher.Decrypt(Guid.CreateVersion7(), 1, encrypted, new MasterKeyRing(_key)).Error.Code.Value.ShouldBe(SecretsErrors.IntegrityFailedCode);
        EnvelopeCipher.Decrypt(_id, 2, encrypted, new MasterKeyRing(_key)).Error.Code.Value.ShouldBe(SecretsErrors.IntegrityFailedCode);
    }

    [Fact]
    public void SEC_U_005_Wrong_master_key_detected()
    {
        var encrypted = EnvelopeCipher.Encrypt(_id, 1, "p@ss"u8, _key);
        using var other = MasterKey.Generate();

        // Same key id claimed, different key material.
        EnvelopeCipher.Decrypt(_id, 1, encrypted with { KeyId = other.KeyId }, new MasterKeyRing(other)).Error.Code.Value
            .ShouldBe(SecretsErrors.IntegrityFailedCode);
        EnvelopeCipher.Decrypt(_id, 1, encrypted, new MasterKeyRing(other)).Error.Code.Value.ShouldBe(SecretsErrors.IntegrityFailedCode);
    }

    [Fact]
    public void SEC_U_009_SecretValue_ToString_hides_value()
    {
        using var value = SecretValue.Copy("top-secret"u8);

        value.ToString().ShouldBe("***");
        $"{value}".ShouldBe("***");
    }

    [Fact]
    public void SEC_U_010_SecretValue_Dispose_zeroes_buffer()
    {
        var value = SecretValue.Copy(Encoding.UTF8.GetBytes("top-secret"));
        value.RawBufferForTests.ToArray().ShouldNotBe(new byte[10]);

        value.Dispose();

        value.RawBufferForTests.ToArray().ShouldBe(new byte[10]);
        Should.Throw<ObjectDisposedException>(() => value.Use(v => v.Length));
    }

    [Fact]
    public void SEC_U_011_SecretRef_Parse()
    {
        var id = Guid.CreateVersion7();
        SecretRef.Parse($"vault:{id:D}").Value.VaultId!.Value.Value.ShouldBe(id);
        var bootstrap = SecretRef.Parse("bootstrap:database.default").Value;
        bootstrap.IsBootstrap.ShouldBeTrue();
        bootstrap.BootstrapName.ShouldBe("database.default");
        bootstrap.ToString().ShouldBe("bootstrap:database.default");

        foreach (var bad in new[] { "vault:x", "foo:bar", "bootstrap:Bad Name", "bootstrap:", "vault:00000000-0000-0000-0000-000000000000", "" })
        {
            SecretRef.Parse(bad).Error.Code.Value.ShouldBe(SecretsErrors.RefInvalidCode, bad);
        }
    }
}

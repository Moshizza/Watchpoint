using Shouldly;
using Watchpoint.Secrets.Contracts;
using Xunit;

namespace Watchpoint.Secrets.Tests;

[Trait("layer", "U")]
[Trait("spec", "SEC")]
public sealed class VaultTests : IDisposable
{
    private readonly SecretsHarness _harness = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SEC_U_006_Value_size_limit()
    {
        var vault = _harness.CreateVault();

        var tooLarge = await vault.CreateAsync("big", SecretKind.Generic, new SecretOwner("Tests", null), new byte[65_537], null, Ct);
        var atLimit = await vault.CreateAsync("max", SecretKind.Generic, new SecretOwner("Tests", null), new byte[65_536], null, Ct);

        tooLarge.Error.Code.Value.ShouldBe(SecretsErrors.ValueTooLargeCode);
        atLimit.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task SEC_U_007_Owner_check_on_read()
    {
        var vault = _harness.CreateVault();
        var conn1 = Guid.CreateVersion7();
        var id = (await vault.CreateAsync("Salto A password", SecretKind.Password, new SecretOwner("Integrations", conn1), "s3cret"u8.ToArray(), null, Ct)).Value;

        var denied = await vault.ReadAsync(id, new SecretOwner("Integrations", Guid.CreateVersion7()), "connect", Ct);
        var otherModule = await vault.ReadAsync(id, new SecretOwner("Identity", conn1), "connect", Ct);
        using var allowed = (await vault.ReadAsync(id, new SecretOwner("Integrations", conn1), "connect", Ct)).Value;

        denied.Error.Code.Value.ShouldBe(SecretsErrors.AccessDeniedCode);
        otherModule.Error.Code.Value.ShouldBe(SecretsErrors.AccessDeniedCode);
        allowed.UseAsString(s => s).ShouldBe("s3cret");
        _harness.Audit.Records.Count(r => r.Action == "denied").ShouldBe(2);
        _harness.Logs.WithEventId(2212).Count().ShouldBe(2);
        (await vault.ReplaceAsync(id, new SecretOwner("Integrations", null), "x"u8.ToArray(), null, Ct)).Error.Code.Value.ShouldBe(SecretsErrors.AccessDeniedCode);
        (await vault.DeleteAsync(id, new SecretOwner("Other", null), Ct)).Error.Code.Value.ShouldBe(SecretsErrors.AccessDeniedCode);
    }

    [Fact]
    public async Task SEC_U_008_Owner_with_null_entity_readable_by_module()
    {
        var vault = _harness.CreateVault();
        var id = (await vault.CreateAsync("signing key", SecretKind.PrivateKey, new SecretOwner("Identity", null), "k"u8.ToArray(), null, Ct)).Value;

        using var value = (await vault.ReadAsync(id, new SecretOwner("Identity", Guid.CreateVersion7()), "sign", Ct)).Value;
        (await vault.ReplaceAsync(id, new SecretOwner("Identity", null), "k2"u8.ToArray(), null, Ct)).IsSuccess.ShouldBeTrue();
        using var replaced = (await vault.ReadAsync(id, new SecretOwner("Identity", null), "sign", Ct)).Value;

        value.UseAsString(s => s).ShouldBe("k");
        replaced.UseAsString(s => s).ShouldBe("k2");
        (await vault.GetMetadataAsync(id, Ct)).Value.Version.ShouldBe(2);
        _harness.Audit.Records.Select(r => r.Action).ShouldBe(["create", "replace"]);
    }
}

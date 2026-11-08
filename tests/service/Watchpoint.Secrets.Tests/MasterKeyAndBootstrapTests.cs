using System.Runtime.Versioning;
using System.Security.Cryptography;
using Shouldly;
using Watchpoint.Secrets.Contracts;
using Watchpoint.Secrets.Internal;
using Xunit;

namespace Watchpoint.Secrets.Tests;

[Trait("layer", "U")]
[Trait("spec", "SEC")]
public sealed class MasterKeyAndBootstrapTests : IDisposable
{
    private readonly SecretsHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public void SEC_U_012_File_key_provider_creates_0400_key()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes");
        }

        var provider = new FileMasterKeyProvider(_harness.Options, _harness.Logger("test"));

        using var created = provider.LoadOrCreate();
        using var loaded = provider.LoadOrCreate();

        new FileInfo(provider.KeyPath).Length.ShouldBe(32);
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(provider.KeyPath).ShouldBe(UnixFileMode.UserRead);
        }
        loaded.KeyId.ShouldBe(created.KeyId);
        _harness.Logs.WithEventId(2202).ShouldHaveSingleItem();
        _harness.Logs.WithEventId(2201).ShouldHaveSingleItem();
        _harness.Logs.WithEventId(2203).ShouldBeEmpty();
    }

    [Fact]
    public void SEC_U_013_File_key_permissions_warning()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes");
        }

        var provider = new FileMasterKeyProvider(_harness.Options, _harness.Logger("test"));
        provider.LoadOrCreate().Dispose();
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(provider.KeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        provider.LoadOrCreate().Dispose();

        _harness.Logs.WithEventId(2203).ShouldHaveSingleItem().Properties["Path"].ShouldBe(provider.KeyPath);
    }

    [Fact]
    public void SEC_U_014_Environment_provider()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var env = new Dictionary<string, string> { [EnvironmentMasterKeyProvider.KeyVariable] = Convert.ToBase64String(key) };
        var provider = new EnvironmentMasterKeyProvider(n => env.GetValueOrDefault(n), _harness.Logger("test"));

        using var loaded = provider.LoadOrCreate();

        loaded.Bytes.ToArray().ShouldBe(key);
        env[EnvironmentMasterKeyProvider.KeyVariable] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(31));
        Should.Throw<SecretsException>(provider.LoadOrCreate).Error.Code.Value.ShouldBe(SecretsErrors.MasterKeyUnavailableCode);
        env.Clear();
        Should.Throw<SecretsException>(provider.LoadOrCreate).Error.Code.Value.ShouldBe(SecretsErrors.MasterKeyUnavailableCode);
        provider.IsAvailable().ShouldBeFalse();
        Directory.Exists(_harness.Options.SecretsDirectory).ShouldBeFalse(); // never creates anything
    }

    [Fact]
    public void SEC_U_015_DPAPI_provider()
    {
        if (OperatingSystem.IsWindows())
        {
            RunDpapi();
        }
        else
        {
            Assert.Skip("DPAPI exists on Windows only (runs in the Windows CI job)");
        }
    }

    [Fact]
    public void SEC_U_016_Bootstrap_store_atomic_write()
    {
        _harness.Bootstrap.Write("database.default", "old-password"u8).IsSuccess.ShouldBeTrue();
        _harness.Bootstrap.AfterTempWrite = () => throw new IOException("simulated crash before rename");

        Should.Throw<IOException>(() => _harness.Bootstrap.Write("database.default", "new-password"u8));

        _harness.Bootstrap.AfterTempWrite = null;
        using var value = _harness.Bootstrap.Read("database.default").Value;
        value.UseAsString(s => s).ShouldBe("old-password");
        File.ReadAllText(_harness.Bootstrap.FilePath).ShouldNotContain("old-password");
        _harness.Bootstrap.Read("nats.token").Error.Code.Value.ShouldBe(SecretsErrors.NotFoundCode);
        _harness.Bootstrap.Write("Bad Name", "x"u8).Error.Code.Value.ShouldBe(SecretsErrors.RefInvalidCode);
    }

    [Fact]
    public void SEC_U_017_Bootstrap_NATS_token_migration()
    {
        var legacy = Path.Combine(_harness.Options.DataDirectory, "nats", "token");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "nats-token-123");

        _harness.Bootstrap.ImportLegacyNatsToken();

        File.Exists(legacy).ShouldBeFalse();
        using var token = _harness.Bootstrap.Read("nats.token").Value;
        token.UseAsString(s => s).ShouldBe("nats-token-123");
        _harness.Logs.WithEventId(2230).ShouldHaveSingleItem();
    }

    [SupportedOSPlatform("windows")]
    private void RunDpapi()
    {
        var provider = new DpapiMasterKeyProvider(_harness.Options, _harness.Logger("test"));

        using var created = provider.LoadOrCreate();
        using var loaded = provider.LoadOrCreate();

        loaded.KeyId.ShouldBe(created.KeyId);
        var stored = File.ReadAllBytes(provider.KeyPath);
        stored.Length.ShouldNotBe(32);
        stored.AsSpan().IndexOf(created.Bytes).ShouldBe(-1); // not the raw key
    }
}

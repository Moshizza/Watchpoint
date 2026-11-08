using Shouldly;
using Watchpoint.Kernel.Results;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Internal;
using Xunit;
using static Watchpoint.Storage.Tests.StorageTestHost;

namespace Watchpoint.Storage.Tests;

[Trait("layer", "U")]
[Trait("spec", "STO")]
public sealed class StoreMapTests
{
    [Fact]
    public void STO_U_001_Default_store_mapping()
    {
        var map = StoreMap.Build(Settings(), [new FakeStorageProvider()], isDevelopment: false).Value;

        map.Bindings.Count.ShouldBe(4);
        foreach (var store in Enum.GetValues<StoreKind>())
        {
            map.Binding(store).ShouldBe(new StoreBinding(store, "postgres", "default"));
        }

        var db = map.Database(StoreKind.History);
        (db.Host, db.Port, db.Database, db.Username, db.Tls, db.MaxPoolSize).ShouldBe(("localhost", 5432, "watchpoint", "watchpoint", TlsMode.Prefer, 50));
        db.PasswordRef.ShouldBe("bootstrap:database.default");
        map.StartupWait.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void STO_U_002_Unknown_database_key_rejected()
    {
        var result = StoreMap.Build(Settings(("Database:Stores:History", "archive")), [new FakeStorageProvider()], isDevelopment: false);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe(StorageErrors.SettingsInvalidCode);
        var field = result.Error.FieldErrors.ShouldHaveSingleItem();
        field.Path.ShouldBe("Database:Stores:History");
        field.Code.ShouldBe(CommonErrorCodes.ValueNotAllowed);
        ((IReadOnlyList<string>)field.Args["allowed"]!).ShouldHaveSingleItem().ShouldBe("default");
    }

    [Fact]
    public void STO_U_003_Unknown_provider_rejected()
    {
        var result = StoreMap.Build(Settings(("Database:Databases:default:Provider", "oracle")), [new FakeStorageProvider()], isDevelopment: true);

        result.Error.Code.Value.ShouldBe(StorageErrors.ProviderUnknownCode);
        result.Error.Args["provider"].ShouldBe("oracle");

        // The holder turns it into a configuration exit (78).
        var holder = Holder(Settings(("Database:Databases:default:Provider", "oracle")));
        Should.Throw<StorageStartupException>(() => holder.Map).ExitCode.ShouldBe(StorageStartupException.Config);
    }

    [Fact]
    public void STO_U_004_Dev_password_refused_in_Production()
    {
        var settings = Settings(("Database:Databases:default:PasswordDev", "dev-only"));

        var production = StoreMap.Build(settings, [new FakeStorageProvider()], isDevelopment: false);
        var development = StoreMap.Build(settings, [new FakeStorageProvider()], isDevelopment: true);

        production.Error.FieldErrors.ShouldHaveSingleItem().Path.ShouldBe("Database:Databases:default:PasswordDev");
        development.Value.Database(StoreKind.Configuration).PasswordDev.ShouldBe("dev-only");
    }

    [Theory]
    [InlineData("10.0.0.5", false)]
    [InlineData("db.example.org", false)]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("/var/run/postgresql", true)]
    public void STO_U_005_Insecure_remote_refused(string host, bool accepted)
    {
        var settings = Settings(("Database:Databases:default:Host", host), ("Database:Databases:default:Tls", "Disable"));

        var result = StoreMap.Build(settings, [new FakeStorageProvider()], isDevelopment: true);

        result.IsSuccess.ShouldBe(accepted);
        if (!accepted)
        {
            result.Error.FieldErrors.ShouldHaveSingleItem().Path.ShouldBe("Database:Databases:default:Tls");

            // Explicitly allowed: accepted with a warning.
            var allowed = StoreMap.Build(
                Settings(("Database:Databases:default:Host", host), ("Database:Databases:default:Tls", "Disable"), ("Database:AllowInsecureRemote", "true")),
                [new FakeStorageProvider()], isDevelopment: true);
            allowed.Value.Warnings.ShouldHaveSingleItem().ShouldContain("TLS disabled");
        }
    }
}

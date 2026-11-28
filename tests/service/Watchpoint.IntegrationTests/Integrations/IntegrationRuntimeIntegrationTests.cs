using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Shouldly;
using Watchpoint.Api.Contracts;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Integrations.Internal.Packages;
using Watchpoint.Integrations.Testing;
using Watchpoint.IntegrationTests.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Model;
using Watchpoint.Model.Contracts;
using Watchpoint.Model.Documents;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing.Containers;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.IntegrationTests.Integrations;

/// <summary>Integration runtime, package side (spec IRT §15, IRT-I-001…004): real PostgreSQL, the real simulator package, HTTP.</summary>
[Trait("layer", "I")]
[Trait("spec", "IRT")]
public sealed class IntegrationRuntimeIntegrationTests(PostgresContainerFixture postgres) : IAsyncDisposable
{
    private static readonly PackageId _simulator = PackageId.FromTrusted(SimulatorPackage.Id);
    private readonly List<IAsyncDisposable> _owned = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }
    }

    [Fact]
    public async Task IRT_I_001_Builtin_simulator_package_verified_and_active_at_start()
    {
        await using var node = await NodeAsync(nameof(IRT_I_001_Builtin_simulator_package_verified_and_active_at_start));
        SimulatorPackage.Stage(AppContext.BaseDirectory, Path.Combine(node.Options.BuiltinPath, "simulator"), node.DevKey);

        await node.Start(); // phase Catalogue, order 10: scan, then watch

        var package = node.Catalog.List().ShouldHaveSingleItem();
        package.Id.ShouldBe(_simulator);
        package.Version.ShouldBe(new SemVersion(1, 0, 0));
        package.State.ShouldBe(PackageState.Active, string.Join("; ", package.Problems));
        package.Source.ShouldBe(PackageInfo.BuiltinSource);
        package.SignedByDevelopmentKey.ShouldBeTrue();
        package.Problems.ShouldBeEmpty();
        package.Connectors.ShouldHaveSingleItem().ServerType.ShouldBe("sim.server");
        package.Isolation.ShouldBe("inProcessAllowed");
        node.Catalog.GetLatestActive(_simulator).Value.ShouldBe(package);

        // Its types are in the type catalogue, and its settings schema is exposed.
        node.Types.GetLatest(TypeId.FromTrusted("sim.server"), 1).IsSuccess.ShouldBeTrue();
        var schema = node.Catalog.GetSettingsSchema(_simulator, package.Version, "sim.server").Value;
        schema.Secrets.ShouldHaveSingleItem().Id.ShouldBe("token");
        node.Logs.WithEventId(1803).ShouldHaveSingleItem();
        node.Logs.WithEventId(1804).ShouldHaveSingleItem();

        // The real connector class loads in the package's load context and is the host's IConnector.
        var (packageDir, manifest) = node.Catalog.GetLoadable(_simulator, package.Version).Value;
        var context = CreateAndRelease(node, packageDir, manifest.Connectors[0].Class, connector =>
            connector.GetType().FullName.ShouldBe("Watchpoint.Integration.Simulator.SimulatorConnector"));
        Collected(context).ShouldBeTrue("the simulator's load context is collected once unused");

        // Readiness: the scan completed.
        using var http = new HttpClient();
        (await http.GetAsync(node.BaseAddress + "health", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        node.Logs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error, "no error was logged while starting");
    }

    [Fact]
    public async Task IRT_I_002_Package_rows_persisted()
    {
        var name = nameof(IRT_I_002_Package_rows_persisted);
        await using var node = await NodeAsync(name);
        SimulatorPackage.Stage(AppContext.BaseDirectory, Path.Combine(node.Options.BuiltinPath, "simulator"), node.DevKey);
        new TestPackage("vendor.broken").WithConnector().WithManifest(m => m["contract"] = "^3.0").WriteWpkg(Path.Combine(node.Options.PackagesPath, "broken.wpkg"), node.DevKey);
        await node.Start();

        var rows = await RowsAsync(node.ConnectionString);
        var simulator = rows.ShouldHaveSingleItem();
        simulator.ShouldBe((SimulatorPackage.Id, "1.0.0", "active", "builtin", true, true));
        (await ScalarAsync<string>(node.ConnectionString, "SELECT problems::text FROM wp_integrations.packages")).ShouldBe("[]");

        // Invalid packages are not persisted (the file is the source of truth; the next scan verifies it again).
        node.Catalog.List().Count.ShouldBe(2);

        // Disabling is persisted and survives a restart: the package comes back Disabled.
        (await node.Catalog.SetEnabledAsync(_simulator, new SemVersion(1, 0, 0), false, UserId.New(), Ct)).IsSuccess.ShouldBeTrue();
        (await RowsAsync(node.ConnectionString)).ShouldHaveSingleItem().ShouldBe((SimulatorPackage.Id, "1.0.0", "disabled", "builtin", true, false));

        await using var restarted = await NodeAsync(name, node);
        await restarted.Start();
        var restartedPackage = restarted.Catalog.Get(_simulator, new SemVersion(1, 0, 0)).Value;
        restartedPackage.State.ShouldBe(PackageState.Disabled, string.Join("; ", restartedPackage.Problems));
        restarted.Types.GetLatest(TypeId.FromTrusted("sim.server"), 1).IsSuccess.ShouldBeTrue("types stay registered");
    }

    [Fact]
    public async Task IRT_I_003_Upload_wpkg_through_API()
    {
        await using var node = await NodeAsync(nameof(IRT_I_003_Upload_wpkg_through_API));
        await node.Start();
        using var http = new HttpClient { BaseAddress = node.BaseAddress }.WithAdminToken();

        // Valid package: 201 with Location, then listed.
        using var valid = Multipart(new TestPackage("vendor.uploaded").WithConnector().ToWpkg(node.DevKey), "vendor.uploaded.wpkg");
        using var created = await http.PostAsync("api/v1/packages", valid, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Ct));
        created.Headers.Location!.ToString().ShouldBe("/api/v1/packages/vendor.uploaded/versions/1.0.0");
        var body = JsonNode.Parse(await created.Content.ReadAsStringAsync(Ct))!;
        body["state"]!.GetValue<string>().ShouldBe("active");
        body["signedByDevelopmentKey"]!.GetValue<bool>().ShouldBeTrue();
        File.Exists(Path.Combine(node.Options.PackagesPath, "vendor.uploaded.wpkg")).ShouldBeTrue();

        var list = JsonNode.Parse(await http.GetStringAsync("api/v1/packages", Ct))!;
        list["items"]!.AsArray().Select(p => p!["id"]!.GetValue<string>()).ShouldBe(["vendor.uploaded"]);
        var details = JsonNode.Parse(await http.GetStringAsync(created.Headers.Location, Ct))!;
        details["connectors"]![0]!["serverType"]!.GetValue<string>().ShouldBe("vendor.uploaded.server");
        var schema = JsonNode.Parse(await http.GetStringAsync("api/v1/packages/vendor.uploaded/versions/1.0.0/connectors/vendor.uploaded.server/settings-schema", Ct))!;
        schema["secrets"]![0]!["id"]!.GetValue<string>().ShouldBe("password");

        // Invalid package: 422 with the problems; the file is kept so that the user can see what was refused.
        using var unsigned = Multipart(new TestPackage("vendor.unsigned").WithConnector().ToWpkg(null), "vendor.unsigned.wpkg");
        using var refused = await http.PostAsync("api/v1/packages", unsigned, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problems = JsonNode.Parse(await refused.Content.ReadAsStringAsync(Ct))!["problems"]!.AsArray();
        problems.ShouldHaveSingleItem()!["code"]!.GetValue<string>().ShouldBe(IntegrationErrors.PackageUnsigned);

        // A file name that could leave the folder: 400 problem details, nothing written.
        using var evil = Multipart([1, 2, 3], "../evil.wpkg");
        using var bad = await http.PostAsync("api/v1/packages", evil, Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        bad.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        JsonNode.Parse(await bad.Content.ReadAsStringAsync(Ct))!["code"]!.GetValue<string>().ShouldBe(IntegrationErrors.PackageFileNameInvalid);
        Directory.GetFiles(node.Root, "evil*", SearchOption.AllDirectories).ShouldBeEmpty();

        // Disable and enable through the API; unknown package → 404.
        using var disabled = await http.PostAsync("api/v1/packages/vendor.uploaded/versions/1.0.0/disable", null, Ct);
        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonNode.Parse(await disabled.Content.ReadAsStringAsync(Ct))!["state"]!.GetValue<string>().ShouldBe("disabled");
        using var enabled = await http.PostAsync("api/v1/packages/vendor.uploaded/versions/1.0.0/enable", null, Ct);
        JsonNode.Parse(await enabled.Content.ReadAsStringAsync(Ct))!["state"]!.GetValue<string>().ShouldBe("active");
        (await http.GetAsync("api/v1/packages/vendor.nothing/versions/1.0.0", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task IRT_I_004_Load_context_isolation_with_two_real_packages()
    {
        await using var node = await NodeAsync(nameof(IRT_I_004_Load_context_isolation_with_two_real_packages));
        const string members = """
            public string Humanized() => Humanizer.StringHumanizeExtensions.Humanize("some_vendor_text");
            public string LibraryVersion() => typeof(Humanizer.StringHumanizeExtensions).Assembly.GetName().Version!.ToString(1);
            """;
        var two = new TestPackage("vendor.two").WithConnector(members, privateDependencies: (NuGetLibraries.HumanizerAssembly, NuGetLibraries.Humanizer2));
        var three = new TestPackage("vendor.three").WithConnector(members, privateDependencies: (NuGetLibraries.HumanizerAssembly, NuGetLibraries.Humanizer3));
        two.WriteWpkg(Path.Combine(node.Options.PackagesPath, "two.wpkg"), node.DevKey);
        three.WriteWpkg(Path.Combine(node.Options.PackagesPath, "three.wpkg"), node.DevKey);
        await node.Start();
        node.Catalog.List().Select(p => (p.Id.Value, p.State)).ShouldBe([("vendor.three", PackageState.Active), ("vendor.two", PackageState.Active)]);

        var contexts = UseBoth(node, two, three);

        Collected(contexts.Two).ShouldBeTrue();
        Collected(contexts.Three).ShouldBeTrue();
    }

    // ------------------------------------------------------------------ helpers

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Two, WeakReference Three) UseBoth(Node node, TestPackage two, TestPackage three)
    {
        var (dirTwo, manifestTwo) = node.Catalog.GetLoadable(PackageId.FromTrusted(two.Id), new SemVersion(1, 0, 0)).Value;
        var (dirThree, manifestThree) = node.Catalog.GetLoadable(PackageId.FromTrusted(three.Id), new SemVersion(1, 0, 0)).Value;
        using var leaseTwo = node.Pool.Acquire(PackageId.FromTrusted(two.Id), new SemVersion(1, 0, 0), dirTwo, two.AssemblyName);
        using var leaseThree = node.Pool.Acquire(PackageId.FromTrusted(three.Id), new SemVersion(1, 0, 0), dirThree, three.AssemblyName);
        var connectorTwo = leaseTwo.CreateConnector(manifestTwo.Connectors[0].Class);
        var connectorThree = leaseThree.CreateConnector(manifestThree.Connectors[0].Class);

        Invoke<string>(connectorTwo, "LibraryVersion").ShouldBe("2");
        Invoke<string>(connectorThree, "LibraryVersion").ShouldBe("3");
        Invoke<string>(connectorTwo, "Humanized").ShouldBe("some vendor text", StringCompareShould.IgnoreCase);
        Invoke<string>(connectorThree, "Humanized").ShouldBe("some vendor text", StringCompareShould.IgnoreCase);
        leaseTwo.Context.Assemblies.Select(a => a.GetName().Name).Order().ShouldBe(["Humanizer", two.AssemblyName]);
        leaseThree.Context.Assemblies.Select(a => a.GetName().Name).Order().ShouldBe(["Humanizer", three.AssemblyName]);
        AssemblyLoadContext.Default.Assemblies.Select(a => a.GetName().Name).ShouldNotContain(NuGetLibraries.HumanizerAssembly, "the host never loads a package's private library");
        return (new WeakReference(leaseTwo.Context), new WeakReference(leaseThree.Context));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndRelease(Node node, string packageDir, string connectorClass, Action<object> use)
    {
        using var lease = node.Pool.Acquire(_simulator, new SemVersion(1, 0, 0), packageDir, ConnectorCodeChecker.SplitClass(connectorClass).Assembly);
        use(lease.CreateConnector(connectorClass));
        return new WeakReference(lease.Context);
    }

    private static bool Collected(WeakReference reference)
    {
        for (var i = 0; i < 20 && reference.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !reference.IsAlive;
    }

    private static T Invoke<T>(object target, string method) =>
        (T)target.GetType().GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!.Invoke(target, null)!;

    private static MultipartFormDataContent Multipart(byte[] wpkg, string fileName)
    {
        var content = new ByteArrayContent(wpkg);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { content, "file", fileName } };
    }

    private static async Task<List<(string Id, string Version, string State, string Source, bool SignedByDev, bool Enabled)>> RowsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT id, version, state, source, signed_by_dev, enabled FROM wp_integrations.packages ORDER BY id, version", connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string, string, string, bool, bool)>();
        while (await reader.ReadAsync(Ct))
        {
            rows.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        }

        return rows;
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>A node with Storage (PostgreSQL), Model and the integration runtime in Development, on temporary folders, serving the package endpoints.</summary>
    private async Task<Node> NodeAsync(string test, Node? previous = null)
    {
        PostgresDatabase db;
        if (previous is null)
        {
            db = await postgres.CreateDatabaseAsync(nameof(IntegrationRuntimeIntegrationTests) + "." + test, Ct);
            _owned.Add(db);
        }
        else
        {
            db = previous.Database;
        }

        var cs = new NpgsqlConnectionStringBuilder(db.ConnectionString);
        var settings = new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
        };
        var node = new Node(db, previous);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders().AddProvider(node.Logs).SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddWatchpointStorage(builder.Configuration, isDevelopment: true);
        builder.Services.AddPostgresStorage();
        builder.Services.AddWatchpointModel();
        builder.Services.AddWatchpointIntegrations(node.Options);
        builder.Services.AddTestApi(builder.Configuration);
        var app = builder.Build();
        app.UseWatchpointApi();
        app.MapPackageEndpoints();
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            Predicate = check => check.Name == Watchpoint.Integrations.Internal.PackageScanHealthCheck.Name,
        });
        node.App = app;
        await app.StartAsync(Ct);
        await app.Services.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);
        return node;
    }

    private sealed class Node : IAsyncDisposable
    {
        public Node(PostgresDatabase database, Node? previous)
        {
            Database = database;
            Root = previous?.Root ?? Directory.CreateTempSubdirectory("wp-irt-i-").FullName;
            DevKey = previous?.DevKey ?? new TestKey("dev-test");
            _ownsKey = previous is null;
            Options = new IntegrationsOptions
            {
                BuiltinPath = Path.Combine(Root, "builtin"),
                PackagesPath = Path.Combine(Root, "integrations"),
                CachePath = Path.Combine(Root, "integrations", ".cache"),
                DevKeysPath = Path.Combine(Root, "integrations", "dev-keys"),
                IsDevelopment = true,
                ProductVersion = new SemVersion(1, 0, 0),
            };
            Directory.CreateDirectory(Options.BuiltinPath);
            Directory.CreateDirectory(Options.PackagesPath);
            DevKey.WriteDevelopmentKey(Options.DevKeysPath);
        }

        public PostgresDatabase Database { get; }

        public string ConnectionString => Database.ConnectionString;

        public string Root { get; }

        public IntegrationsOptions Options { get; }

        private readonly bool _ownsKey;

        public TestKey DevKey { get; }

        public LogCollector Logs { get; } = new();

        public WebApplication App { get; set; } = null!;

        public Uri BaseAddress => new(App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/') + "/");

        public PackageCatalog Catalog => App.Services.GetRequiredService<PackageCatalog>();

        public PackageLoadContextPool Pool => App.Services.GetRequiredService<PackageLoadContextPool>();

        public ITypeCatalog Types => App.Services.GetRequiredService<ITypeCatalog>();

        public Task Start() => App.Services.GetRequiredService<IPackageWatcher>().StartAsync(Ct);

        public async ValueTask DisposeAsync()
        {
            await App.Services.GetRequiredService<PackageWatcher>().DisposeAsync();
            await App.DisposeAsync();
            if (_ownsKey)
            {
                DevKey.Dispose();
            }

            Logs.Dispose();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Watchpoint.Host.Dev;
using Watchpoint.Hosting;
using Watchpoint.Identity.Contracts;
using Watchpoint.Integrations.Testing;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Registry;
using Watchpoint.Registry.Contracts;
using Watchpoint.Storage.Contracts;
using Watchpoint.Storage.Postgres;
using Watchpoint.Testing;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>The development stack and seed (spec HST §8, §9).</summary>
[Trait("layer", "I")]
[Trait("spec", "HST")]
[Collection(SerialHostProcesses.Name)]
public sealed partial class DevStackTests(PostgresContainerFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Written in T1.02, enabled by T1.16 (spec HST §14): the seed's site, locations, users and simulator connection are
    /// created through the modules' contracts, and the connection imports its devices on its first connect. The host
    /// runs with its own NATS server and the simulator staged as a builtin package signed with a development key.
    /// </summary>
    [Fact]
    [Trait("requires", "T1.16,T1.17,T1.18")]
    public async Task HST_I_008_Dev_seed_creates_site_users_simulator_connection()
    {
        await using var db = await postgres.CreateDatabaseAsync(nameof(DevStackTests), Ct);
        var data = HostProcess.NewDataDirectory("i008");
        try
        {
            using (var key = new TestKey("dev-i008"))
            {
                key.WriteDevelopmentKey(Path.Combine(data, "integrations", "dev-keys"));
                SimulatorPackage.Stage(AppContext.BaseDirectory, Path.Combine(data, "builtin", "simulator"), key);
            }

            await using var host = await HostProcess.StartWithNatsAsync("i008", new Dictionary<string, JsonNode>
            {
                ["Database"] = HostProcess.DatabaseSection(db.ConnectionString),
                ["Dev"] = new JsonObject { ["SeedFile"] = Path.Combine(RepositoryRoot(), "deploy", "dev", "seed", "sample-site.json") },
                ["Integrations"] = new JsonObject { ["BuiltinPath"] = Path.Combine(data, "builtin") },
            }, Ct, dataDirectory: data);
            try
            {
                await SeededAsync(host, db.ConnectionString);
            }
            catch (Exception ex) when (ex is TestAssertionException or ShouldAssertException)
            {
                throw new TestAssertionException(ex.Message + Environment.NewLine + "Host output (tail):" + Environment.NewLine + host.Output + Environment.NewLine + "Host log (tail):" + Environment.NewLine + LogTail(data), ex);
            }
        }
        finally
        {
            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the host may still hold a file on Windows.
            }
        }
    }

    private static string LogTail(string data)
    {
        var logs = Path.Combine(data, "logs");
        var files = Directory.Exists(logs) ? Directory.GetFiles(logs, "*", SearchOption.AllDirectories) : [];
        return string.Join(Environment.NewLine, files.SelectMany(f =>
        {
            using var stream = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n').TakeLast(80);
        }));
    }

    private static async Task SeededAsync(HostProcess host, string db)
    {
        using var http = new HttpClient();
        await Eventually.UntilAsync(
            () =>
            {
                host.HasExited.ShouldBeFalse(host.StoppedDescription());
                try
                {
                    using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{host.MainPort}/health/ready"), Ct);
                    return response.StatusCode == HttpStatusCode.OK;
                }
                catch (HttpRequestException)
                {
                    return false;
                }
            },
            TimeSpan.FromSeconds(60),
            "the seeded node is ready",
            TimeSpan.FromMilliseconds(200),
            Ct);

        // The simulator connection connects, describes its inventory and imports it (autoImport): bounded wait.
        await Eventually.UntilAsync(
            () =>
            {
                host.HasExited.ShouldBeFalse(host.StoppedDescription());
                using var connection = new NpgsqlConnection(db);
                connection.Open();
                using var command = new NpgsqlCommand("SELECT count(*) FROM wp_registry.devices", connection);
                return (long)command.ExecuteScalar()! > 0;
            },
            TimeSpan.FromSeconds(60),
            "the simulator's devices are imported",
            TimeSpan.FromMilliseconds(250),
            Ct);

        // The seeded node's data, read through the modules' public contracts on the same database.
        var cs = new NpgsqlConnectionStringBuilder(db);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Databases:default:Host"] = cs.Host,
            ["Database:Databases:default:Port"] = cs.Port.ToString(CultureInfo.InvariantCulture),
            ["Database:Databases:default:Name"] = cs.Database,
            ["Database:Databases:default:Username"] = cs.Username,
            ["Database:Databases:default:PasswordDev"] = cs.Password,
            ["Database:Databases:default:Tls"] = "Disable",
            ["Database:AllowInsecureRemote"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new NodeSettings { Node = new NodeSection { Environment = NodeEnvironment.Development } });
        services.AddWatchpointStorage(configuration, isDevelopment: true);
        services.AddPostgresStorage();
        services.AddIdentityModule();
        Watchpoint.Model.ModelModule.AddWatchpointModel(services);
        services.AddWatchpointRegistry(SiteId.New());
        await using var provider = services.BuildServiceProvider();

        // Watchpoint opens database connections asynchronously only: open the pool's first connection that way
        // (the modules' first reads below load their snapshots synchronously from a pooled connection).
        await provider.GetRequiredService<IStorageStartup>().MigrateAsync(Ct);

        // The administrator of the seed can sign in.
        var seed = DevSeedReader.Read(await File.ReadAllBytesAsync(Path.Combine(RepositoryRoot(), "deploy", "dev", "seed", "sample-site.json"), Ct)).Value;
        var admin = seed.Users.Single(u => u.Administrator);
        var signIn = await provider.GetRequiredService<IAuthenticationService>().SignInLocalAsync(
            new SignInRequest(admin.Login, admin.Password, new ClientContext(ClientKind.Desktop, "hst-i-008")), Ct);
        signIn.IsSuccess.ShouldBeTrue();

        // The credentials are accepted; an administrator then enrols a second factor first (IDN, T1.18).
        signIn.Value.ShouldBeOfType<SignInOutcome.MfaEnrolmentRequired>();
        provider.GetRequiredService<IUserDirectory>().FindByLogin(admin.Login).Value.Administrator.ShouldBeTrue();

        // Site and locations exist; the simulator's devices were discovered and imported.
        var registry = provider.GetRequiredService<IRegistryReader>();
        registry.Site.Name.Default.ShouldBe(seed.Site.Name);
        registry.Site.DefaultTimezone.ShouldBe(seed.Site.TimeZone);
        registry.GetLocationTree().Select(l => l.Name.Default).ShouldBe(seed.Locations.Select(l => l.Name), ignoreOrder: true);
        var floor = DevSeed.FindLocation(seed, "a0", registry).ShouldNotBeNull();
        var device = registry.QueryDevices(new DeviceQuery(), new PageRequest(1)).Items.ShouldHaveSingleItem();
        registry.GetServer(device.ServerId).Value.LocationId.ShouldBe(floor);
    }

    [Fact]
    public async Task HST_I_010_Development_stack_is_pinned_and_consistent()
    {
        var root = RepositoryRoot();
        var compose = await File.ReadAllTextAsync(Path.Combine(root, "deploy", "dev", "compose.yml"), Ct);
        var versions = (await File.ReadAllLinesAsync(Path.Combine(root, "deploy", "versions.env"), Ct))
            .Where(l => l.Contains('=', StringComparison.Ordinal) && !l.StartsWith('#'))
            .ToDictionary(l => l[..l.IndexOf('=', StringComparison.Ordinal)], l => l[(l.IndexOf('=', StringComparison.Ordinal) + 1)..]);

        // Every image of the compose file is pinned by digest and is the image the tests run against.
        var images = ImageLine().Matches(compose).Select(m => m.Groups[1].Value).ToList();
        images.ShouldBe([ContainerImages.TimescaleDb, ContainerImages.Nats]);
        images.ShouldAllBe(i => PinnedImage().IsMatch(i));
        versions["TIMESCALEDB_IMAGE"].ShouldBe(ContainerImages.TimescaleDb);
        versions["NATS_IMAGE"].ShouldBe(ContainerImages.Nats);

        // The server binary of the tests is the version of the image, with the checksums of versions.env.
        versions["NATS_SERVER_VERSION"].ShouldBe(NatsServerBinary.Version);
        ContainerImages.Nats.ShouldStartWith($"nats:{NatsServerBinary.Version}-");
        foreach (var (asset, sha256) in NatsServerBinary.PinnedArchives)
        {
            var key = "NATS_SERVER_SHA256_" + asset[..asset.IndexOf('.', StringComparison.Ordinal)].Replace('-', '_').ToUpperInvariant();
            versions[key].ShouldBe(sha256);
        }

        // Published on loopback only; the development settings and seed are valid and consistent with the stack.
        PortLine().Matches(compose).Select(m => m.Groups[1].Value).ShouldAllBe(p => p.StartsWith("127.0.0.1:", StringComparison.Ordinal));
        var settings = new NodeSettings();
        new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "deploy", "dev", "watchpoint.dev.json")).Build().Bind(settings);
        settings.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(settings)).ShouldBeEmpty();
        settings.Node.Environment.ShouldBe(NodeEnvironment.Development);
        settings.Nats.Mode.ShouldBe(NatsMode.External);
        compose.ShouldContain($"\"--auth\", \"{settings.Nats.Token}\"");
        var seed = DevSeedReader.Read(await File.ReadAllBytesAsync(Path.Combine(root, settings.Dev.SeedFile!), Ct));
        seed.IsSuccess.ShouldBeTrue();
        seed.Value.Users.ShouldContain(u => u.Administrator);
    }

    private static string RepositoryRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "global.json")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Repository root (global.json) not found.");
    }

    [GeneratedRegex(@"^\s+image:\s*(\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex ImageLine();

    [GeneratedRegex(@"^[a-z0-9./-]+:[A-Za-z0-9._-]+@sha256:[0-9a-f]{64}$")]
    private static partial Regex PinnedImage();

    [GeneratedRegex(@"^\s+- ""([0-9.:]+)""\s*$", RegexOptions.Multiline)]
    private static partial Regex PortLine();
}

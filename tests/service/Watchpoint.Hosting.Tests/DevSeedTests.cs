using System.Text;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Host.Dev;
using Watchpoint.Kernel.Results;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Hosting.Tests;

/// <summary>Development seed: validation before anything is applied, order, emptiness, failures (spec HST §9).</summary>
[Trait("layer", "U")]
[Trait("spec", "HST")]
public sealed class DevSeedTests : IDisposable
{
    private const string Valid = """
        {
          "site": { "name": "Dev site", "timeZone": "Europe/Paris" },
          "locations": [
            { "key": "hq", "name": "HQ", "kind": "site" },
            { "key": "a", "parent": "hq", "name": "Building A", "kind": "building" }
          ],
          "users": [ { "login": "admin", "password": "Dev-Admin-1!", "administrator": true } ],
          "connections": [
            { "package": "watchpoint.integration.simulator", "serverType": "sim.server", "name": "Simulator",
              "location": "a", "settings": { "doors": 20 }, "secrets": {}, "autoImport": true }
          ]
        }
        """;

    private readonly string _file = Path.Combine(Path.GetTempPath(), "wp-seed-" + Guid.NewGuid().ToString("N") + ".json");
    private readonly LogCollector _logs = new();
    private readonly LoggerFactory _factory = new();

    public DevSeedTests() => _factory.AddProvider(_logs);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _factory.Dispose();
        File.Delete(_file);
    }

    [Fact]
    public async Task HST_U_025_Seed_document_validated()
    {
        // The valid document maps completely.
        var seed = DevSeedReader.Read(Encoding.UTF8.GetBytes(Valid)).Value;
        seed.Site.ShouldBe(new DevSeedSite("Dev site", "Europe/Paris"));
        seed.Locations.ShouldBe([new DevSeedLocation("hq", null, "HQ", "site"), new DevSeedLocation("a", "hq", "Building A", "building")]);
        seed.Users.ShouldHaveSingleItem().ShouldBe(new DevSeedUser("admin", "Dev-Admin-1!", "admin", true));
        seed.Users[0].ToString().ShouldNotContain("Dev-Admin");
        var connection = seed.Connections.ShouldHaveSingleItem();
        connection.Location.ShouldBe("a");
        connection.AutoImport.ShouldBeTrue();
        connection.Settings["doors"]!.GetValue<int>().ShouldBe(20);

        // Unknown location key: a validation error naming the path, and nothing is applied.
        var unknown = Valid.Replace("\"location\": \"a\"", "\"location\": \"nowhere\"", StringComparison.Ordinal);
        var error = DevSeedReader.Read(Encoding.UTF8.GetBytes(unknown)).Error;
        error.Code.Value.ShouldBe("host.seed.invalid");
        error.Kind.ShouldBe(ErrorKind.Validation);
        var field = error.FieldErrors.ShouldHaveSingleItem();
        field.Path.ShouldBe("/connections/0/location");
        field.Code.Value.ShouldBe("host.seed.reference_unknown");

        var step = new RecordingStep("users");
        await File.WriteAllTextAsync(_file, unknown, Ct);
        await Task(step).ExecuteAsync(Ct);
        step.Applied.ShouldBe(0);
        var log = _logs.WithEventId(1140).ShouldHaveSingleItem();
        log.Level.ShouldBe(LogLevel.Error);
        log.Properties["Step"].ShouldBe("validate");
        ((string)log.Properties["Error"]!).ShouldContain("/connections/0/location");

        // Other reference and schema problems.
        Paths(Valid.Replace("\"parent\": \"hq\"", "\"parent\": \"zz\"", StringComparison.Ordinal)).ShouldBe(["/locations/1/parent"]);
        Paths(Valid.Replace("\"key\": \"a\"", "\"key\": \"hq\"", StringComparison.Ordinal))
            .ShouldBe(["/locations/1/key", "/connections/0/location"]);
        Paths(Valid.Replace("\"kind\": \"building\"", "\"kind\": \"castle\"", StringComparison.Ordinal)).ShouldContain("/locations/1/kind");
        Paths(Valid.Replace("\"site\":", "\"extra\": 1, \"site\":", StringComparison.Ordinal)).ShouldNotBeEmpty();
        Paths("{ not json").ShouldBe([string.Empty]);
        Paths("{}").ShouldNotBeEmpty();
    }

    [Fact]
    public async Task HST_U_043_Seed_applied_in_order_once_and_never_fatal()
    {
        await File.WriteAllTextAsync(_file, Valid, Ct);
        var order = new List<string>();

        // Sections in the specified order, whatever the registration order; a section without a step is reported.
        var users = new RecordingStep("users", order);
        var site = new RecordingStep("site", order);
        var locations = new RecordingStep("locations", order);
        await Task(users, locations, site).ExecuteAsync(Ct);
        order.ShouldBe(["site", "locations", "users"]);
        _logs.WithEventId(1141).ShouldHaveSingleItem().Properties["Section"].ShouldBe("connections");
        _logs.WithEventId(1143).ShouldHaveSingleItem();
        _logs.WithEventId(1140).ShouldBeEmpty();
        _logs.Entries.ShouldAllBe(e => !e.RenderedMessage.Contains("Dev-Admin", StringComparison.Ordinal));

        // The store is not empty: nothing is applied.
        _logs.Clear();
        order.Clear();
        await Task(site, new RecordingStep("users", order) { HasData = true }).ExecuteAsync(Ct);
        order.ShouldBeEmpty();
        _logs.WithEventId(1142).ShouldHaveSingleItem().Properties["Section"].ShouldBe("users");

        // A failing or throwing step is logged (HST1140), stops the seed and does not stop the node.
        _logs.Clear();
        var failing = new RecordingStep("locations", order) { Result = Error.Conflict("registry.location.conflict") };
        await Task(site, failing, users).ExecuteAsync(Ct);
        order.ShouldBe(["site", "locations"]);
        var failed = _logs.WithEventId(1140).ShouldHaveSingleItem();
        failed.Properties["Step"].ShouldBe("locations");
        failed.Properties["Error"].ShouldBe("registry.location.conflict");

        _logs.Clear();
        await Task(new RecordingStep("site") { Throw = new InvalidOperationException("boom") }).ExecuteAsync(Ct);
        _logs.WithEventId(1140).ShouldHaveSingleItem().Properties["Step"].ShouldBe("site");

        // A missing file is a logged failure too.
        _logs.Clear();
        File.Delete(_file);
        await Task(site).ExecuteAsync(Ct);
        _logs.WithEventId(1140).ShouldHaveSingleItem().Properties["Step"].ShouldBe("read");

        var task = Task();
        task.Phase.ShouldBe(StartupPhase.Configuration);
        task.Order.ShouldBe(1000);
    }

    private static List<string> Paths(string json) =>
        [.. DevSeedReader.Read(Encoding.UTF8.GetBytes(json)).Error.FieldErrors.Select(f => f.Path).Distinct()];

    private DevSeedStartupTask Task(params IDevSeedStep[] steps)
    {
        var settings = new NodeSettings { Node = { Environment = NodeEnvironment.Development }, Dev = { SeedFile = _file } };
        return new DevSeedStartupTask(settings, steps, new Logger<DevSeedStartupTask>(_factory));
    }

    private sealed class RecordingStep(string section, List<string>? order = null) : IDevSeedStep
    {
        public string Section => section;

        public bool HasData { get; init; }

        public Result Result { get; init; } = Result.Success();

        public Exception? Throw { get; init; }

        public int Applied { get; private set; }

        public Task<bool> HasDataAsync(DevSeedDocument seed, CancellationToken ct) => System.Threading.Tasks.Task.FromResult(HasData);

        public Task<Result> ApplyAsync(DevSeedDocument seed, CancellationToken ct)
        {
            Applied++;
            order?.Add(section);
            return Throw is null ? System.Threading.Tasks.Task.FromResult(Result) : throw Throw;
        }
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Watchpoint.IntegrationTests.Host;
using Watchpoint.Integrations.Testing;
using Watchpoint.Testing.Async;
using Watchpoint.Testing.Containers;
using Xunit;

namespace Watchpoint.IntegrationTests.Api;

/// <summary>
/// A real host (process) with the development seed and the simulator connection, shared by the tests of one class
/// (they run one after the other). MFA is not required so the seeded administrator signs in with a password.
/// </summary>
public sealed class ApiNodeFixture(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private PostgresDatabase? _db;
    private HostProcess? _host;
    private string? _data;

    internal HostProcess Host => _host ?? throw new InvalidOperationException("The host is not started.");

    internal string DataDirectory => _data!;

    internal Uri BaseAddress => new($"http://127.0.0.1:{Host.MainPort}/");

    internal string ConnectionString => _db!.ConnectionString;

    /// <summary>A desktop administrator token, signed in once.</summary>
    internal string AdminToken { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        _db = await postgres.CreateDatabaseAsync("ApiNode", ct);
        _data = HostProcess.NewDataDirectory("api");
        using (var key = new TestKey("dev-api"))
        {
            key.WriteDevelopmentKey(Path.Combine(_data, "integrations", "dev-keys"));
            SimulatorPackage.Stage(AppContext.BaseDirectory, Path.Combine(_data, "builtin", "simulator"), key);
        }

        _host = await HostProcess.StartWithNatsAsync("api", new Dictionary<string, JsonNode>
        {
            ["Database"] = HostProcess.DatabaseSection(_db.ConnectionString),
            ["Dev"] = new JsonObject { ["SeedFile"] = Path.Combine(RepositoryRoot(), "deploy", "dev", "seed", "sample-site.json") },
            ["Integrations"] = new JsonObject { ["BuiltinPath"] = Path.Combine(_data, "builtin") },
            ["Identity"] = new JsonObject { ["Mfa"] = new JsonObject { ["RequireFor"] = "none" } },
        }, ct, dataDirectory: _data);

        using var anonymous = Client();
        await PollAsync(async () =>
        {
            try
            {
                using var ready = await anonymous.GetAsync("health/ready", ct);
                return ready.IsSuccessStatusCode;
            }
            catch (HttpRequestException)
            {
                return false;
            }
        }, TestTimeouts.Container * 3, "the host is ready", ct);

        AdminToken = await SignInAsync("desktop", ct);
        using var admin = Client(AdminToken);
        await PollAsync(async () =>
            JsonNode.Parse(await admin.GetStringAsync("api/v1/devices?limit=1", ct))!["items"]!.AsArray().Count > 0,
            TestTimeouts.Container * 3, "the simulator devices are imported", ct);
    }

    internal HttpClient Client(string? token = null)
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    /// <summary>Signs the seeded administrator in from <paramref name="clientKind"/> and returns the access token.</summary>
    internal async Task<string> SignInAsync(string clientKind, CancellationToken ct)
    {
        using var anonymous = Client();
        using var response = await anonymous.PostAsJsonAsync("api/v1/auth/sign-in",
            new { login = "admin", password = "Dev-Watchpoint-1!", clientKind, clientInstanceId = "api-tests-" + Guid.NewGuid().ToString("N") }, ct);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return body["accessToken"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Sign-in did not return tokens: " + body.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        try
        {
            if (_data is not null)
            {
                Directory.Delete(_data, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 200 ms until it holds or <paramref name="timeout"/> elapses.</summary>
    internal static async Task PollAsync(Func<Task<bool>> condition, TimeSpan timeout, string because, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        while (!await condition())
        {
            if (TimeProvider.System.GetElapsedTime(started) >= timeout)
            {
                throw new Watchpoint.Testing.TestAssertionException($"Timed out after {timeout.TotalSeconds:0} s: {because}.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), TimeProvider.System, ct);
        }
    }

    internal static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}

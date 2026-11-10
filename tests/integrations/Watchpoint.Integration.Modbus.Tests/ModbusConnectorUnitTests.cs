using System.Text.Json;
using Shouldly;
using Watchpoint.Integration.Testing.Fakes;
using Watchpoint.Kernel.Testing;
using Watchpoint.Wptypes;
using Xunit;

namespace Watchpoint.Integration.Modbus.Tests;

public sealed class ModbusConnectorUnitTests
{
    [Fact]
    public async Task MODB_U_029_Connector_DiscoveryAndPoll()
    {
        var tempTemplatePath = Path.Combine(Path.GetTempPath(), $"modbus-test-{Guid.NewGuid():N}.json");
        var templateContent = """
        {
          "id": "tpl-test",
          "name": { "en": "Test Template" },
          "devices": [
            {
              "key": "door-1",
              "type": "ac.door",
              "points": [
                { "property": "state", "table": "coil", "address": 0, "dataType": "bit" }
              ]
            }
          ]
        }
        """;
        await File.WriteAllTextAsync(tempTemplatePath, templateContent, TestContext.Current.CancellationToken);

        try
        {
            await using var server = new ModbusTestServer();
            server.Coils[0] = true;

            var settingsJson = $$"""
            {
              "host": "127.0.0.1",
              "port": {{server.Port}},
              "units": [
                {
                  "unitId": 1,
                  "template": "{{tempTemplatePath.Replace("\\", "/")}}"
                }
              ]
            }
            """;
            var settings = JsonDocument.Parse(settingsJson).RootElement;
            var time = TestTime.CreateProvider();
            var connector = new ModbusConnector();

            using var context = new TestConnectorContext(settings, time, new Dictionary<string, string>());
            var initRes = await connector.InitializeAsync(context, CancellationToken.None);
            initRes.IsSuccess.ShouldBeTrue();

            var snapshot = await connector.DescribeAsync(CancellationToken.None);
            snapshot.Servers.Count.ShouldBe(2);
            snapshot.Devices.Count.ShouldBe(1);
            snapshot.Devices[0].NativeId.ShouldBe("door-1");

            await connector.StartAsync(CancellationToken.None);
            for (var i = 0; i < 50 && context.Sink.Properties.Count == 0; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(100));
                await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
            }

            context.Sink.Properties.Count.ShouldBeGreaterThan(0);

            await connector.StopAsync(CancellationToken.None);
        }
        finally
        {
            if (File.Exists(tempTemplatePath)) File.Delete(tempTemplatePath);
        }
    }

    [Fact]
    public void MODB_U_030_Package_WptypesValidate()
    {
        var repoRoot = FindRepoRoot();
        var pkgPath = Path.Combine(repoRoot, "src", "integrations", "Watchpoint.Integration.Modbus");
        var typesPath = Path.Combine(repoRoot, "types");
        var asmPath = typeof(ModbusConnector).Assembly.Location;

        var stagedDir = PackageStager.Stage(pkgPath, asmPath);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            var exitCode = WptypesCli.Run(["validate", stagedDir, "--with", typesPath, "--strict-i18n"], output, error);
            exitCode.ShouldBe(WptypesCli.Ok, output.ToString() + error.ToString());
        }
        finally
        {
            Directory.Delete(stagedDir, true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "global.json")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("Could not find repository root.");
    }
}

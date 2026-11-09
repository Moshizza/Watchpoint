using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Testing;
using Watchpoint.Wptypes;
using Xunit;

namespace Watchpoint.Integration.Simulator.Tests;

public sealed class DiagnosticsAndPackageTests
{
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }
        throw new DirectoryNotFoundException("Could not find repo root with global.json");
    }

    [Fact]
    public async Task SIM_U_028_Diagnostics_actions()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", doors = 1 });
        var context = new TestConnectorContext(json, time);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var injectArgs = JsonSerializer.SerializeToElement(new { device = "door-0001", @event = "test.event" });
        var res1 = await connector.RunActionAsync("injectEvent", injectArgs, CancellationToken.None);
        res1.IsSuccess.ShouldBeTrue();
        context.Sink.Events.Any(e => e.EventPath == "test.event").ShouldBeTrue();

        var propArgs = JsonSerializer.SerializeToElement(new { device = "door-0001", path = "doorPosition.position", value = "open" });
        var res2 = await connector.RunActionAsync("setProperty", propArgs, CancellationToken.None);
        res2.IsSuccess.ShouldBeTrue();
        connector.EmbeddedSite!.GetDeviceState("door-0001")!.Properties["doorPosition.position"].ShouldBe("open");

        var res3 = await connector.RunActionAsync("pauseActivity", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        res3.IsSuccess.ShouldBeTrue();
        var res4 = await connector.RunActionAsync("resumeActivity", JsonSerializer.SerializeToElement(new { }), CancellationToken.None);
        res4.IsSuccess.ShouldBeTrue();

        var failArgs = JsonSerializer.SerializeToElement(new
        {
            failures = new[] { new { command = "openable.open", mode = "refuse", probability = 1.0 } },
        });
        var res5 = await connector.RunActionAsync("setFailures", failArgs, CancellationToken.None);
        res5.IsSuccess.ShouldBeTrue();

        var diag = await connector.GetDiagnosticsAsync(CancellationToken.None);
        diag.Any(d => d.Id == "mode").ShouldBeTrue();
        diag.Any(d => d.Id == "devicesCount").ShouldBeTrue();
    }

    [Fact]
    public async Task SIM_U_029_Regenerate_reports_inventory_change()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", profile = "office", seed = 1 });
        var context = new TestConnectorContext(json, time);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var regenArgs = JsonSerializer.SerializeToElement(new { seed = 999 });
        var res = await connector.RunActionAsync("regenerate", regenArgs, CancellationToken.None);
        res.IsSuccess.ShouldBeTrue();

        context.Sink.InventoryChanges.Count.ShouldBeGreaterThanOrEqualTo(1);
        var changed = context.Sink.InventoryChanges.Last();
        changed.Servers.Any(s => s.VendorName.Contains("seed 999")).ShouldBeTrue();
    }

    [Fact]
    public void SIM_U_030_Translations_present_for_all_keys_in_en_fr_ar()
    {
        var repoRoot = FindRepoRoot();
        var pkgPath = Path.Combine(repoRoot, "src", "integrations", "Watchpoint.Integration.Simulator");
        var typesPath = Path.Combine(repoRoot, "types");
        var asmPath = typeof(SimulatorConnector).Assembly.Location;

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
}

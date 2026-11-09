using Shouldly;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Integration.Simulator.Engine.Behaviours;
using Watchpoint.Model.Documents;
using Watchpoint.Model.Documents.Library;
using Xunit;

namespace Watchpoint.Integration.Simulator.Tests;

public sealed class SiteGeneratorTests
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
    public void SIM_U_001_Deterministic_generation()
    {
        var settings1 = new SimulatorSettings(Seed: 42, Profile: SiteProfile.Office);
        var settings2 = new SimulatorSettings(Seed: 42, Profile: SiteProfile.Office);

        var inv1 = SiteGenerator.Generate(settings1);
        var inv2 = SiteGenerator.Generate(settings2);

        inv1.Servers.Count.ShouldBe(inv2.Servers.Count);
        inv1.Devices.Count.ShouldBe(inv2.Devices.Count);

        for (int i = 0; i < inv1.Devices.Count; i++)
        {
            inv1.Devices[i].NativeId.ShouldBe(inv2.Devices[i].NativeId);
            inv1.Devices[i].VendorName.ShouldBe(inv2.Devices[i].VendorName);
            inv1.Devices[i].TypeId.ShouldBe(inv2.Devices[i].TypeId);
            inv1.Devices[i].Links.Count.ShouldBe(inv2.Devices[i].Links.Count);
        }
    }

    [Fact]
    public void SIM_U_002_Profile_counts()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Office);
        var inv = SiteGenerator.Generate(settings);

        int doors = inv.Devices.Count(d => d.TypeId == "ac.door");
        int readers = inv.Devices.Count(d => d.TypeId == "ac.reader");
        int areas = inv.Devices.Count(d => d.TypeId == "intrusion.area");
        int zones = inv.Devices.Count(d => d.TypeId == "intrusion.zone");
        int inputs = inv.Devices.Count(d => d.TypeId == "generic.binary-input");
        int outputs = inv.Devices.Count(d => d.TypeId == "generic.binary-output");
        int analogs = inv.Devices.Count(d => d.TypeId == "generic.analog-input");

        doors.ShouldBe(20);
        readers.ShouldBe(20);
        areas.ShouldBe(2);
        zones.ShouldBe(10);
        inputs.ShouldBe(4);
        outputs.ShouldBe(4);
        analogs.ShouldBe(2);
    }

    [Fact]
    public void SIM_U_003_Custom_counts_and_limits()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 20_001);
        Should.Throw<ArgumentOutOfRangeException>(() => SiteGenerator.Generate(settings));

        var validSettings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 15, Areas: 3, ZonesPerArea: 4, Inputs: 5, Outputs: 6, Analogs: 7);
        var inv = SiteGenerator.Generate(validSettings);

        inv.Devices.Count(d => d.TypeId == "ac.door").ShouldBe(15);
        inv.Devices.Count(d => d.TypeId == "intrusion.area").ShouldBe(3);
        inv.Devices.Count(d => d.TypeId == "intrusion.zone").ShouldBe(12);
        inv.Devices.Count(d => d.TypeId == "generic.binary-input").ShouldBe(5);
        inv.Devices.Count(d => d.TypeId == "generic.binary-output").ShouldBe(6);
        inv.Devices.Count(d => d.TypeId == "generic.analog-input").ShouldBe(7);
    }

    [Fact]
    public void SIM_U_004_Sub_servers_per_100_doors()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Campus);
        var inv = SiteGenerator.Generate(settings);

        inv.Servers.Count.ShouldBe(5);
        inv.Servers.Select(s => s.NativeId).ShouldContain("sim-root");
        inv.Servers.Select(s => s.NativeId).ShouldContain("sim-ctrl-01");
        inv.Servers.Select(s => s.NativeId).ShouldContain("sim-ctrl-04");

        var door1 = inv.Devices.First(d => d.NativeId == "door-0001");
        door1.ServerNativeId.ShouldBe("sim-ctrl-01");

        var door150 = inv.Devices.First(d => d.NativeId == "door-0150");
        door150.ServerNativeId.ShouldBe("sim-ctrl-02");

        var door350 = inv.Devices.First(d => d.NativeId == "door-0350");
        door350.ServerNativeId.ShouldBe("sim-ctrl-04");
    }

    [Fact]
    public void SIM_U_005_Optional_capability_unsupported_every_10th_door()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Office);
        var inv = SiteGenerator.Generate(settings);

        var door1 = inv.Devices.First(d => d.NativeId == "door-0001");
        door1.UnsupportedOptionalCapabilities.ShouldBeEmpty();

        var door10 = inv.Devices.First(d => d.NativeId == "door-0010");
        door10.UnsupportedOptionalCapabilities.ShouldContain("tamper");

        var door20 = inv.Devices.First(d => d.NativeId == "door-0020");
        door20.UnsupportedOptionalCapabilities.ShouldContain("tamper");
    }

    [Fact]
    public void SIM_U_006_Inventory_valid_against_standard_types()
    {
        var repoRoot = FindRepoRoot();
        var typesPath = Path.Combine(repoRoot, "types");
        var reader = DocumentReaderFactory.FromEmbeddedSchemas();
        var libFolder = LibraryLoader.Load(typesPath, reader);

        var settings = new SimulatorSettings(Profile: SiteProfile.Office);
        var inv = SiteGenerator.Generate(settings);

        foreach (var dev in inv.Devices)
        {
            libFolder.Types.Any(t => t.Document.Id.Value == dev.TypeId).ShouldBeTrue($"Unknown type {dev.TypeId}");
        }
    }

    [Fact]
    public void SIM_U_015_Holder_names_include_Arabic_and_accented_names()
    {
        bool hasArabic = HolderCatalog.Names.Any(n => n.Any(c => c >= 0x0600 && c <= 0x06FF));
        bool hasAccents = HolderCatalog.Names.Any(n => n.Any(c => "éèêëàâäôöùûüçñ".Contains(char.ToLowerInvariant(c))));

        hasArabic.ShouldBeTrue();
        hasAccents.ShouldBeTrue();
        HolderCatalog.Names.Count.ShouldBeGreaterThanOrEqualTo(200);
    }
}

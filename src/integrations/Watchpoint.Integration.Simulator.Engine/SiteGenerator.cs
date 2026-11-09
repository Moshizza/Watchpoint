namespace Watchpoint.Integration.Simulator.Engine;

public static class SiteGenerator
{
    public static (int doors, int readersPerDoor, int areas, int zonesPerArea, int inputs, int outputs, int analogs)
        ResolveCounts(SimulatorSettings settings)
    {
        return settings.Profile switch
        {
            SiteProfile.Office => (20, settings.ReadersPerDoor, 2, 5, 4, 4, 2),
            SiteProfile.Bank => (12, settings.ReadersPerDoor, 4, 8, 8, 8, 4),
            SiteProfile.Campus => (400, settings.ReadersPerDoor, 40, 10, 100, 100, 50),
            SiteProfile.Custom => (
                settings.Doors ?? 20,
                settings.ReadersPerDoor,
                settings.Areas,
                settings.ZonesPerArea,
                settings.Inputs,
                settings.Outputs,
                settings.Analogs),
            _ => (20, 1, 2, 5, 4, 4, 2),
        };
    }

    public static SimInventory Generate(SimulatorSettings settings)
    {
        var (doors, readersPerDoor, areas, zonesPerArea, inputs, outputs, analogs) = ResolveCounts(settings);

        if (doors < 0 || doors > 20_000)
        {
            throw new ArgumentOutOfRangeException(nameof(settings), "doors must be between 0 and 20000");
        }

        var servers = new List<SimServer>();
        var devices = new List<SimDevice>();

        string profileName = settings.Profile.ToString().ToLowerInvariant();
        servers.Add(new SimServer(
            NativeId: "sim-root",
            ParentNativeId: null,
            TypeId: "sim.server",
            TypeVersion: "1.0",
            VendorName: $"Simulator ({profileName}, seed {settings.Seed})",
            VendorAttributes: new Dictionary<string, object?>()));

        int subServerCount = (doors + 99) / 100;
        if (doors == 0)
        {
            subServerCount = 0;
        }

        for (int i = 1; i <= subServerCount; i++)
        {
            servers.Add(new SimServer(
                NativeId: $"sim-ctrl-{i:D2}",
                ParentNativeId: "sim-root",
                TypeId: "generic.server",
                TypeVersion: "1.0",
                VendorName: $"Controller {i:D2}",
                VendorAttributes: new Dictionary<string, object?>()));
        }

        for (int i = 1; i <= doors; i++)
        {
            int ctrlIndex = (i - 1) / 100 + 1;
            string serverNativeId = subServerCount > 0 ? $"sim-ctrl-{ctrlIndex:D2}" : "sim-root";
            string doorId = $"door-{i:D4}";

            var unsupported = new List<string>();
            if (i % 10 == 0)
            {
                unsupported.Add("tamper");
            }

            devices.Add(new SimDevice(
                NativeId: doorId,
                ServerNativeId: serverNativeId,
                ParentNativeId: null,
                TypeId: "ac.door",
                TypeVersion: "1.0",
                VendorName: $"Door {i:D4}",
                VendorAttributes: new Dictionary<string, object?>(),
                UnsupportedOptionalCapabilities: unsupported,
                Links: []));

            for (int r = 1; r <= readersPerDoor; r++)
            {
                string readerId = $"{doorId}-r{r}";
                string inOut = r == 1 ? "IN" : "OUT";
                devices.Add(new SimDevice(
                    NativeId: readerId,
                    ServerNativeId: serverNativeId,
                    ParentNativeId: doorId,
                    TypeId: "ac.reader",
                    TypeVersion: "1.0",
                    VendorName: $"Door {i:D4} reader {inOut}",
                    VendorAttributes: new Dictionary<string, object?>(),
                    UnsupportedOptionalCapabilities: [],
                    Links: []));
            }
        }

        for (int a = 1; a <= areas; a++)
        {
            string areaId = $"area-{a:D3}";
            devices.Add(new SimDevice(
                NativeId: areaId,
                ServerNativeId: "sim-root",
                ParentNativeId: null,
                TypeId: "intrusion.area",
                TypeVersion: "1.0",
                VendorName: $"Area {a:D3}",
                VendorAttributes: new Dictionary<string, object?>(),
                UnsupportedOptionalCapabilities: [],
                Links: []));

            for (int z = 1; z <= zonesPerArea; z++)
            {
                string zoneId = $"zone-{a:D3}-{z:D2}";
                devices.Add(new SimDevice(
                    NativeId: zoneId,
                    ServerNativeId: "sim-root",
                    ParentNativeId: null,
                    TypeId: "intrusion.zone",
                    TypeVersion: "1.0",
                    VendorName: $"Zone {a:D3}-{z:D2}",
                    VendorAttributes: new Dictionary<string, object?>(),
                    UnsupportedOptionalCapabilities: [],
                    Links: [new SimLink("memberOf", areaId)]));
            }
        }

        for (int i = 1; i <= inputs; i++)
        {
            string inId = $"in-{i:D4}";
            devices.Add(new SimDevice(
                NativeId: inId,
                ServerNativeId: "sim-root",
                ParentNativeId: null,
                TypeId: "generic.binary-input",
                TypeVersion: "1.0",
                VendorName: $"Input {i:D4}",
                VendorAttributes: new Dictionary<string, object?> { ["trueSeverity"] = "warning" },
                UnsupportedOptionalCapabilities: [],
                Links: []));
        }

        for (int i = 1; i <= outputs; i++)
        {
            string outId = $"out-{i:D4}";
            devices.Add(new SimDevice(
                NativeId: outId,
                ServerNativeId: "sim-root",
                ParentNativeId: null,
                TypeId: "generic.binary-output",
                TypeVersion: "1.0",
                VendorName: $"Output {i:D4}",
                VendorAttributes: new Dictionary<string, object?>(),
                UnsupportedOptionalCapabilities: [],
                Links: []));
        }

        for (int i = 1; i <= analogs; i++)
        {
            string anaId = $"ana-{i:D4}";
            devices.Add(new SimDevice(
                NativeId: anaId,
                ServerNativeId: "sim-root",
                ParentNativeId: null,
                TypeId: "generic.analog-input",
                TypeVersion: "1.0",
                VendorName: $"Analog {i:D4}",
                VendorAttributes: new Dictionary<string, object?>
                {
                    ["unit"] = "Cel",
                    ["warningAbove"] = 28.0,
                    ["alarmAbove"] = 32.0,
                    ["hysteresis"] = 0.5,
                },
                UnsupportedOptionalCapabilities: [],
                Links: []));
        }

        return new SimInventory(servers, devices);
    }
}

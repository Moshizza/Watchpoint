using System.Text.Json;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Simulator.Mapping;

public static class SimMapping
{
    public static InventorySnapshot ToInventorySnapshot(SimInventory inventory)
    {
        var servers = inventory.Servers.Select(s => new InventoryServerItem(
            NativeId: s.NativeId,
            ParentNativeId: s.ParentNativeId,
            TypeId: s.TypeId,
            TypeVersion: s.TypeVersion,
            VendorName: s.VendorName,
            VendorAttributes: ToWpValueDict(s.VendorAttributes))).ToList();

        var devices = inventory.Devices.Select(d => new InventoryDeviceItem(
            NativeId: d.NativeId,
            ServerNativeId: d.ServerNativeId,
            ParentNativeId: d.ParentNativeId,
            TypeId: d.TypeId,
            TypeVersion: d.TypeVersion,
            VendorName: d.VendorName,
            VendorAttributes: ToWpValueDict(d.VendorAttributes),
            UnsupportedOptionalCapabilities: d.UnsupportedOptionalCapabilities,
            Links: d.Links.Select(l => new InventoryLinkItem(l.Kind, l.ToNativeId, l.ToServerNativeId)).ToList())).ToList();

        return new InventorySnapshot(servers, devices, inventory.IsComplete);
    }

    public static IReadOnlyDictionary<string, WpValue> ToWpValueDict(IReadOnlyDictionary<string, object?> dict)
    {
        var result = new Dictionary<string, WpValue>(StringComparer.Ordinal);
        foreach (var (k, v) in dict)
        {
            result[k] = ToWpValue(v);
        }
        return result;
    }

    public static WpValue ToWpValue(object? value)
    {
        return value switch
        {
            null => WpValue.Null,
            bool b => WpValue.Bool(b),
            int i => WpValue.Int(i),
            long l => WpValue.Int(l),
            double d => WpValue.Decimal((decimal)d),
            float f => WpValue.Decimal((decimal)f),
            decimal dec => WpValue.Decimal(dec),
            string s => WpValue.String(s),
            JsonElement elem => ElementToWpValue(elem),
            _ => WpValue.String(value.ToString() ?? string.Empty),
        };
    }

    private static WpValue ElementToWpValue(JsonElement elem)
    {
        return elem.ValueKind switch
        {
            JsonValueKind.True => WpValue.Bool(true),
            JsonValueKind.False => WpValue.Bool(false),
            JsonValueKind.Number => elem.TryGetInt64(out var l) ? WpValue.Int(l) : WpValue.Decimal(elem.GetDecimal()),
            JsonValueKind.String => WpValue.String(elem.GetString()!),
            JsonValueKind.Null => WpValue.Null,
            _ => WpValue.String(elem.GetRawText()),
        };
    }

    public static SimulatorSettings ParseSettings(JsonElement element)
    {
        string mode = element.TryGetProperty("mode", out var m) ? m.GetString() ?? "embedded" : "embedded";
        string url = element.TryGetProperty("url", out var u) ? u.GetString() ?? "ws://localhost:5090/sim" : "ws://localhost:5090/sim";

        SiteProfile profile = SiteProfile.Office;
        if (element.TryGetProperty("profile", out var p) && Enum.TryParse<SiteProfile>(p.GetString(), true, out var prof))
        {
            profile = prof;
        }

        int? doors = element.TryGetProperty("doors", out var d) && d.TryGetInt32(out var di) ? di : null;
        int readersPerDoor = element.TryGetProperty("readersPerDoor", out var r) && r.TryGetInt32(out var ri) ? ri : 1;
        int areas = element.TryGetProperty("areas", out var a) && a.TryGetInt32(out var ai) ? ai : 2;
        int zonesPerArea = element.TryGetProperty("zonesPerArea", out var z) && z.TryGetInt32(out var zi) ? zi : 5;
        int inputs = element.TryGetProperty("inputs", out var inp) && inp.TryGetInt32(out var inpi) ? inpi : 4;
        int outputs = element.TryGetProperty("outputs", out var outp) && outp.TryGetInt32(out var outpi) ? outpi : 4;
        int analogs = element.TryGetProperty("analogs", out var ana) && ana.TryGetInt32(out var anai) ? anai : 2;
        int seed = element.TryGetProperty("seed", out var s) && s.TryGetInt32(out var si) ? si : 1;

        SiteActivity activity = SiteActivity.Normal;
        if (element.TryGetProperty("activity", out var act) && Enum.TryParse<SiteActivity>(act.GetString(), true, out var acti))
        {
            activity = acti;
        }

        TimeSpan? commandLatency = null;
        if (element.TryGetProperty("commandLatency", out var lat))
        {
            try
            {
                commandLatency = System.Xml.XmlConvert.ToTimeSpan(lat.GetString()!);
            }
            catch
            {
            }
        }

        var failures = new List<FailureRule>();
        if (element.TryGetProperty("failures", out var fails) && fails.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in fails.EnumerateArray())
            {
                string cmd = item.GetProperty("command").GetString()!;
                string modeStr = item.GetProperty("mode").GetString()!;
                double prob = item.GetProperty("probability").GetDouble();
                if (Enum.TryParse<FailureMode>(modeStr, true, out var fMode))
                {
                    failures.Add(new FailureRule(cmd, fMode, prob));
                }
            }
        }

        string? scenario = element.TryGetProperty("scenario", out var sc) ? sc.GetString() : null;
        string timeZone = element.TryGetProperty("timeZone", out var tz) ? tz.GetString() ?? "Europe/Paris" : "Europe/Paris";
        string? token = element.TryGetProperty("token", out var tok) ? tok.GetString() : null;

        return new SimulatorSettings(
            Mode: mode,
            Url: url,
            Profile: profile,
            Doors: doors,
            ReadersPerDoor: readersPerDoor,
            Areas: areas,
            ZonesPerArea: zonesPerArea,
            Inputs: inputs,
            Outputs: outputs,
            Analogs: analogs,
            Seed: seed,
            Activity: activity,
            CommandLatency: commandLatency,
            Failures: failures,
            Scenario: scenario,
            TimeZone: timeZone,
            Token: token);
    }
}

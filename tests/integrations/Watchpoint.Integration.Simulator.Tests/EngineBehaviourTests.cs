using Shouldly;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Integration.Simulator.Engine.Behaviours;
using Watchpoint.Kernel.Testing;
using Xunit;

namespace Watchpoint.Integration.Simulator.Tests;

public sealed class EngineBehaviourTests
{
    [Fact]
    public void SIM_U_007_Door_passage_sequence()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 1, Activity: SiteActivity.Stress);
        var site = new SimulatedSite(settings);

        var events = new List<SimEventRecord>();
        var changes = new List<SimChangeRecord>();
        site.EventEmitted += e => events.Add(e);
        site.ChangeEmitted += c => changes.Add(c);

        var time = TestTime.CreateProvider();
        bool granted = false;
        for (int i = 0; i < 50; i++)
        {
            time.Advance(TimeSpan.FromMilliseconds(250));
            site.Tick(TimeSpan.FromMilliseconds(250), time.GetUtcNow());
            if (events.Any(e => e.Event == "accessEvents.granted"))
            {
                granted = true;
                break;
            }
        }

        granted.ShouldBeTrue();
        changes.Any(c => c.Props.TryGetValue("doorPosition.position", out var pos) && (string?)pos == "open").ShouldBeTrue();

        time.Advance(TimeSpan.FromSeconds(10));
        site.Tick(TimeSpan.FromSeconds(10), time.GetUtcNow());

        var doorState = site.GetDeviceState("door-0001");
        doorState!.Properties["doorPosition.position"].ShouldBe("closed");
    }

    [Fact]
    public void SIM_U_008_Held_open_after_heldOpenAfter()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 1);
        var site = new SimulatedSite(settings);

        var events = new List<SimEventRecord>();
        site.EventEmitted += e => events.Add(e);

        var time = TestTime.CreateProvider();
        var doorState = site.GetDeviceState("door-0001")!;
        doorState.IsHeldOpen = true;
        doorState.HeldOpenTimer = TimeSpan.FromSeconds(40);
        doorState.Properties["doorPosition.position"] = "heldOpen";
        site.EmitEvent("door-0001", "doorPosition.heldOpen", new Dictionary<string, object?>(), time.GetUtcNow());

        events.Any(e => e.Event == "doorPosition.heldOpen").ShouldBeTrue();
        doorState.Properties["doorPosition.position"].ShouldBe("heldOpen");

        time.Advance(TimeSpan.FromSeconds(45));
        site.Tick(TimeSpan.FromSeconds(45), time.GetUtcNow());

        doorState.Properties["doorPosition.position"].ShouldBe("closed");
    }

    [Fact]
    public void SIM_U_009_Forced_without_grant()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 1);
        var site = new SimulatedSite(settings);

        var events = new List<SimEventRecord>();
        site.EventEmitted += e => events.Add(e);

        var time = TestTime.CreateProvider();
        var doorState = site.GetDeviceState("door-0001")!;
        doorState.IsForced = true;
        doorState.DoorCloseTimer = TimeSpan.FromSeconds(10);
        doorState.Properties["doorPosition.position"] = "forced";
        site.EmitEvent("door-0001", "doorPosition.forced", new Dictionary<string, object?>(), time.GetUtcNow());

        events.Any(e => e.Event == "doorPosition.forced").ShouldBeTrue();
        doorState.Properties["doorPosition.position"].ShouldBe("forced");

        time.Advance(TimeSpan.FromSeconds(15));
        site.Tick(TimeSpan.FromSeconds(15), time.GetUtcNow());

        doorState.Properties["doorPosition.position"].ShouldBe("closed");
    }

    [Fact]
    public void SIM_U_010_Activity_rates_within_tolerance()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 50, Activity: SiteActivity.Busy, Seed: 12345);
        var site = new SimulatedSite(settings);

        int passages = 0;
        site.EventEmitted += e =>
        {
            if (e.Event == "accessEvents.granted") passages++;
        };

        var time = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        var delta = TimeSpan.FromSeconds(1);
        int totalSeconds = 3600 * 2;

        for (int i = 0; i < totalSeconds; i++)
        {
            site.Tick(delta, time);
            time += delta;
        }

        double expected = 60.0 * 2 * 50;
        passages.ShouldBeInRange((int)(expected * 0.70), (int)(expected * 1.30));
    }

    [Fact]
    public void SIM_U_011_Busy_hours_vs_off_hours()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Office, TimeZone: "UTC");
        var site = new SimulatedSite(settings);

        var busyTime = new DateTimeOffset(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
        var offTime = new DateTimeOffset(2026, 11, 10, 22, 0, 0, TimeSpan.Zero);

        site.IsBusyHour(busyTime).ShouldBeTrue();
        site.IsBusyHour(offTime).ShouldBeFalse();
    }

    [Fact]
    public void SIM_U_012_Area_arming_schedule()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Office, Activity: SiteActivity.Calm);
        var site = new SimulatedSite(settings);
        site.PauseActivity();

        var areaState = site.GetDeviceState("area-001")!;
        areaState.AreaState = "arming";
        areaState.ExitDelayTimer = TimeSpan.FromSeconds(30);

        IntrusionBehaviour.TickArea(areaState, TimeSpan.FromSeconds(31), DateTimeOffset.UtcNow, ActivityRates.ForActivity(SiteActivity.Calm), 1.0, site);

        areaState.AreaState.ShouldBe("full");
        areaState.Properties["armable.state"].ShouldBe("full");
    }

    [Fact]
    public void SIM_U_013_Zone_activation_while_armed_triggers_area_alarm()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Office);
        var site = new SimulatedSite(settings);

        var areaState = site.GetDeviceState("area-001")!;
        areaState.AreaState = "full";
        areaState.Properties["armable.state"] = "full";

        var zoneState = site.GetDeviceState("zone-001-01")!;
        var time = TestTime.CreateProvider();

        zoneState.ZoneActiveTimer = TimeSpan.FromSeconds(5);
        zoneState.Properties["zone.state"] = "active";
        areaState.AreaState = "alarm";
        areaState.Properties["armable.state"] = "alarm";
        site.EmitEvent(areaState.Device.NativeId, "armable.alarm", new Dictionary<string, object?> { ["causeZone"] = zoneState.Device.NativeId }, time.GetUtcNow());

        areaState.AreaState.ShouldBe("alarm");
        areaState.Properties["armable.state"].ShouldBe("alarm");
    }

    [Fact]
    public void SIM_U_014_Analog_thresholds_reached_by_heat_spike()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Analogs: 1);
        var site = new SimulatedSite(settings);

        var anaState = site.GetDeviceState("ana-0001")!;
        anaState.HeatSpikeTimer = TimeSpan.FromMinutes(5);

        var time = new DateTimeOffset(2026, 11, 10, 14, 0, 0, TimeSpan.Zero);
        var rates = ActivityRates.ForActivity(SiteActivity.Normal);

        AnalogBehaviour.Tick(anaState, TimeSpan.FromSeconds(15), time, rates, 1.0, site);

        anaState.AnalogValue.ShouldBeGreaterThan(32.0);
        anaState.AnalogValue.ShouldBeGreaterThan(28.0);
    }
}

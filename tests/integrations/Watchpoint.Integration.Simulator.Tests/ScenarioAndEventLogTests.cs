using System.Text.Json;
using Shouldly;
using Watchpoint.Integration.Simulator.Engine;
using Watchpoint.Integration.Simulator.Engine.Scenario;
using Watchpoint.Kernel.Testing;
using Xunit;

namespace Watchpoint.Integration.Simulator.Tests;

public sealed class ScenarioAndEventLogTests
{
    [Fact]
    public void SIM_U_023_Scenario_timeline_executes_in_order_and_loops()
    {
        string json = """
        {
          "loop": true,
          "steps": [
            { "at": "PT1S", "device": "door-0001", "set": { "doorPosition.position": "forced" } },
            { "at": "PT2S", "device": "door-0001", "event": "doorPosition.forced" },
            { "at": "PT3S", "device": "door-0001", "set": { "doorPosition.position": "closed" } }
          ]
        }
        """;

        var player = ScenarioPlayer.TryParse(json);
        player.ShouldNotBeNull();

        var step1 = player.Advance(TimeSpan.FromSeconds(1));
        step1.Count.ShouldBe(1);
        step1[0].Set!["doorPosition.position"].ShouldBe("forced");

        var step2 = player.Advance(TimeSpan.FromSeconds(1));
        step2.Count.ShouldBe(1);
        step2[0].Event.ShouldBe("doorPosition.forced");

        var step3 = player.Advance(TimeSpan.FromSeconds(1));
        step3.Count.ShouldBe(1);
        step3[0].Set!["doorPosition.position"].ShouldBe("closed");

        var step4 = player.Advance(TimeSpan.FromSeconds(1));
        step4.Count.ShouldBe(1);
        step4[0].Set!["doorPosition.position"].ShouldBe("forced");
    }

    [Fact]
    public void SIM_U_024_Scenario_disables_random_activity_unless_withRandom()
    {
        string noRandomJson = """
        { "steps": [{ "at": "PT10S", "device": "door-0001", "set": { "doorPosition.position": "open" } }] }
        """;
        var playerNoRandom = ScenarioPlayer.TryParse(noRandomJson);
        playerNoRandom!.WithRandom.ShouldBeFalse();

        string withRandomJson = """
        { "withRandom": true, "steps": [] }
        """;
        var playerWithRandom = ScenarioPlayer.TryParse(withRandomJson);
        playerWithRandom!.WithRandom.ShouldBeTrue();
    }

    [Fact]
    public void SIM_U_025_Event_sequence_numbers_strictly_increasing()
    {
        var settings = new SimulatorSettings(Profile: SiteProfile.Custom, Doors: 1);
        var site = new SimulatedSite(settings);

        ulong seq1 = site.EmitEvent("door-0001", "ev1", new Dictionary<string, object?>(), DateTimeOffset.UtcNow);
        ulong seq2 = site.EmitEvent("door-0001", "ev2", new Dictionary<string, object?>(), DateTimeOffset.UtcNow);
        ulong seq3 = site.EmitEvent("door-0001", "ev3", new Dictionary<string, object?>(), DateTimeOffset.UtcNow);

        seq2.ShouldBeGreaterThan(seq1);
        seq3.ShouldBeGreaterThan(seq2);
    }

    [Fact]
    public async Task SIM_U_026_Catch_up_returns_events_after_cursor_once()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", doors = 1 });
        var context = new TestConnectorContext(json, time);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        var site = connector.EmbeddedSite!;
        ulong seq1 = site.EmitEvent("door-0001", "ev1", new Dictionary<string, object?>(), time.GetUtcNow());
        ulong seq2 = site.EmitEvent("door-0001", "ev2", new Dictionary<string, object?>(), time.GetUtcNow());
        ulong seq3 = site.EmitEvent("door-0001", "ev3", new Dictionary<string, object?>(), time.GetUtcNow());

        context.Sink.Events.Clear();
        await context.Store.SetAsync("cursor", BitConverter.GetBytes(seq1), CancellationToken.None);

        await connector.CatchUpAsync(CancellationToken.None);

        context.Sink.Events.Count(e => e.EventPath == "ev2").ShouldBe(1);
        context.Sink.Events.Count(e => e.EventPath == "ev3").ShouldBe(1);
        context.Sink.Events.Any(e => e.EventPath == "ev1").ShouldBeFalse();

        var newCursor = await context.Store.GetAsync("cursor", CancellationToken.None);
        BitConverter.ToUInt64(newCursor!).ShouldBe(seq3);

        var eventsAfterOld = site.GetEventsAfter(0);
        eventsAfterOld.Count.ShouldBe(3);
    }

    [Fact]
    public async Task SIM_U_027_Cursor_saved_only_after_flush()
    {
        var time = TestTime.CreateProvider();
        var json = JsonSerializer.SerializeToElement(new { mode = "embedded", doors = 1 });
        var context = new TestConnectorContext(json, time);

        var connector = new SimulatorConnector();
        await connector.InitializeAsync(context, CancellationToken.None);
        await connector.StartAsync(CancellationToken.None);

        connector.EmbeddedSite!.EmitEvent("door-0001", "ev1", new Dictionary<string, object?>(), time.GetUtcNow());

        await connector.CatchUpAsync(CancellationToken.None);

        context.Store.FlushCountAtLastSave.ShouldBeGreaterThan(0);
        context.Store.FlushCountAtLastSave.ShouldBe(context.Sink.FlushCount);
    }
}

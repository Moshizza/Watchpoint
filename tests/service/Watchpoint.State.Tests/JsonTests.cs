using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.State.Contracts;
using Watchpoint.State.Internal;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class JsonTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void STA_U_033_Json_form()
    {
        var state = BuildState(serverUnavailable: false);

        var json = DeviceStateJson.ToJson(state);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        root.GetProperty("id").GetString().ShouldBe(state.EntityId.ToString());
        root.GetProperty("kind").GetString().ShouldBe("device");
        root.GetProperty("type").GetProperty("id").GetString().ShouldBe("ac.door");
        root.GetProperty("type").GetProperty("version").GetString().ShouldBe("1.0.0");
        root.GetProperty("overall").GetString().ShouldBe("alarm");
        root.GetProperty("underlying").GetString().ShouldBe("alarm");
        root.GetProperty("cause").GetString().ShouldBe("doorPosition.position");
        root.GetProperty("serverUnavailable").GetBoolean().ShouldBeFalse();
        root.GetProperty("health").GetProperty("connectivity").GetString().ShouldBe("online");
        var property = root.GetProperty("properties").GetProperty("doorPosition.position");
        property.GetProperty("value").GetProperty("k").GetString().ShouldBe("enum");
        property.GetProperty("value").GetProperty("v").GetString().ShouldBe("forced");
        property.GetProperty("quality").GetString().ShouldBe("good");
        property.GetProperty("sequence").GetInt64().ShouldBe(1234);
        root.GetProperty("lastEvents").GetProperty("doorPosition.forced").GetProperty("severity").GetString().ShouldBe("alarm");
        root.GetProperty("sequence").GetInt64().ShouldBe(1234);
        root.GetProperty("updatedAt").GetString().ShouldNotBeNull();
    }

    [Fact]
    public void STA_U_034_Effective_quality_stale_when_server_unavailable()
    {
        var state = BuildState(serverUnavailable: true);

        var dto = DeviceStateJson.ToDto(state);

        dto.Properties["doorPosition.position"].Quality.ShouldBe(Quality.Stale);
        state.Properties[TestTypes.Position].Quality.ShouldBe(Quality.Good); // the stored quality is unchanged
    }

    private static DeviceState BuildState(bool serverUnavailable)
    {
        var property = new PropertyState(TestTypes.Position, WpValue.Enum("forced"), Quality.Good, At, At, At, 1234, null);
        var lastEvent = new LastEvent(TestTypes.ForcedEvent, At, Severity.Alarm, 1234);
        var health = new HealthState(Connectivity.Online, [], null, At, At);
        var summary = new StateSummary(OverallState.Alarm, OverallState.Alarm, TestTypes.Position, new Dictionary<PropertyPath, Severity>());
        return new DeviceState(
            Guid.NewGuid(),
            EntityKind.Device,
            TestTypes.DoorV1,
            new Dictionary<PropertyPath, PropertyState> { [TestTypes.Position] = property },
            health,
            summary,
            serverUnavailable,
            new Dictionary<EventPath, LastEvent> { [TestTypes.ForcedEvent] = lastEvent },
            1234,
            At);
    }
}

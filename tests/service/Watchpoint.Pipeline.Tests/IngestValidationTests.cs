using System.Globalization;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.Pipeline.Internal.Ingest;
using Watchpoint.Testing.Ids;
using Xunit;

namespace Watchpoint.Pipeline.Tests;

/// <summary>Validation, normalisation and change detection of the ingest (spec PIP §7.1, §7.2).</summary>
[Trait("layer", "U")]
[Trait("spec", "PIP")]
public sealed class IngestValidationTests : IAsyncDisposable
{
    private readonly RecordingSubscriber _state = new("state", 100);
    private readonly IngestHarness _harness;

    public IngestValidationTests() => _harness = new IngestHarness(null, _state);

    public ValueTask DisposeAsync() => _harness.DisposeAsync();

    [Fact]
    public async Task PIP_U_021_Malformed_observation_rejected()
    {
        await _harness.StartAsync();
        var both = _harness.PositionOf(_harness.Door, "open") with { Server = _harness.Server };
        var neither = _harness.PositionOf(_harness.Door, "open") with { Device = null };
        var noProperty = _harness.PositionOf(_harness.Door, "open") with { Property = null };
        var contradictory = _harness.Event(_harness.Door) with { Property = IngestHarness.Position };
        var noHealth = _harness.Health(_harness.Door, Connectivity.Online) with { Health = null };

        // Rejected in the result, never thrown; the valid observation of the same batch is accepted.
        var result = await _harness.IngestAsync(both, neither, noProperty, contradictory, noHealth, _harness.PositionOf(_harness.Door, "open"));

        result.Accepted.ShouldBe(1);
        result.Rejected.Select(r => r.ObservationId).ShouldBe([both.Id, neither.Id, noProperty.Id, contradictory.Id, noHealth.Id]);
        result.Rejected.ShouldAllBe(r => r.Error.Code.Value == PipelineErrors.ObservationMalformedCode);
        _harness.Records().Count.ShouldBe(1);
        _harness.Counter("watchpoint.pipeline.observations", "outcome", "rejected").ShouldBe(5);
        _harness.Counter("watchpoint.pipeline.observations", "code", PipelineErrors.ObservationMalformedCode).ShouldBe(5);

        // Logged once per connection and code, with a sample (1620), not once per observation.
        _harness.Logs.WithEventId(1620).Count().ShouldBe(1);
    }

    [Fact]
    public async Task PIP_U_022_Unknown_target_rejected()
    {
        await _harness.StartAsync();
        var unknownDevice = _harness.PositionOf(TestIds.Device(999), "open");
        var unknownServer = _harness.Health(_harness.Door, Connectivity.Online) with { Device = null, Server = TestIds.Server(999) };

        var result = await _harness.IngestAsync(unknownDevice, unknownServer);

        result.Accepted.ShouldBe(0);
        result.Rejected.Count.ShouldBe(2);
        result.Rejected.ShouldAllBe(r => r.Error.Code.Value == "pipeline.target.unknown");
        _harness.Records().ShouldBeEmpty();
    }

    [Fact]
    public async Task PIP_U_023_Foreign_target_rejected()
    {
        await _harness.StartAsync();
        var connectionB = TestIds.Connection(2);
        var deviceOfB = TestIds.Device(50);
        _harness.Directory.Devices[deviceOfB] = new IngestTarget(connectionB, true, IngestHarness.DoorType, new HashSet<CapabilityId>());

        // Connection A reports a device of connection B.
        var result = await _harness.IngestAsync(_harness.PositionOf(deviceOfB, "open"));

        result.Rejected.Single().Error.Code.Value.ShouldBe("pipeline.target.foreign");
        _harness.Records().ShouldBeEmpty();

        // B itself may report it.
        var own = await _harness.IngestAsync(_harness.PositionOf(deviceOfB, "open") with { Connection = connectionB });
        own.Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task PIP_U_024_Disabled_device_dropped_silently()
    {
        await _harness.StartAsync();
        var disabled = TestIds.Device(60);
        _harness.Directory.Devices[disabled] = new IngestTarget(_harness.Connection, false, IngestHarness.DoorType, new HashSet<CapabilityId>());

        var result = await _harness.IngestAsync(_harness.PositionOf(disabled, "open"), _harness.Event(disabled));

        result.Counts().ShouldBe((0, 0, 0, 0), "a disabled target is not an error and counts nowhere in the result");
        result.Rejected.ShouldBeEmpty();
        _harness.Counter("watchpoint.pipeline.observations", "outcome", "disabled").ShouldBe(2);
        _harness.Records().ShouldBeEmpty();
        _harness.Logs.WithEventId(1620).ShouldBeEmpty();
    }

    [Fact]
    public async Task PIP_U_025_Unknown_property_rejected()
    {
        await _harness.StartAsync();
        var property = _harness.Property(_harness.Door, PropertyPath.FromTrusted("doorPosition.colour"), WpValue.String("red"));
        var @event = _harness.Event(_harness.Door, EventPath.FromTrusted("doorPosition.exploded"));

        var result = await _harness.IngestAsync(property, @event);

        result.Rejected.Select(r => r.Error.Code.Value).ShouldBe(["model.property.not_found", "model.event.not_found"]);
        _harness.Records().ShouldBeEmpty();
    }

    [Fact]
    public async Task PIP_U_026_Unsupported_optional_capability_rejected()
    {
        await _harness.StartAsync();
        var withoutTamper = TestIds.Device(70);
        _harness.Directory.Devices[withoutTamper] = new IngestTarget(
            _harness.Connection, true, IngestHarness.DoorType, new HashSet<CapabilityId> { CapabilityId.FromTrusted("tamper") });

        // The type has the optional capability; this device declares it unsupported.
        var rejected = await _harness.IngestAsync(_harness.Property(withoutTamper, IngestHarness.Tampered, WpValue.Bool(true)));
        var accepted = await _harness.IngestAsync(_harness.Property(_harness.Door, IngestHarness.Tampered, WpValue.Bool(true)));

        rejected.Rejected.Single().Error.Code.Value.ShouldBe("model.property.not_found");
        accepted.Accepted.ShouldBe(1);
    }

    [Fact]
    public async Task PIP_U_027_Value_normalised_by_MOD()
    {
        await _harness.StartAsync();
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-FR");
        try
        {
            // A string for an enum property, and a temperature in °F for a property in °C.
            var result = await _harness.IngestAsync(
                _harness.Property(_harness.Door, IngestHarness.Position, WpValue.String("open")),
                _harness.Property(_harness.Door, IngestHarness.Temperature, WpValue.Decimal(98.6m)) with { ReportedUnit = "[degF]" },
                _harness.Property(_harness.Door, IngestHarness.Position, WpValue.String("ajar")),
                _harness.Property(_harness.Door, IngestHarness.Temperature, WpValue.Decimal(1m)) with { ReportedUnit = "m" });

            result.Accepted.ShouldBe(2);
            result.Rejected.Select(r => r.Error.Code.Value).ShouldBe(["model.value.enum_unknown", "model.value.unit_incompatible"]);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        var records = _harness.Records();
        records.Count.ShouldBe(2);
        _harness.PropertyBody(records[0]).Value.ShouldBe(WpValue.Enum("open"));
        var temperature = _harness.PropertyBody(records[1]).Value;
        temperature.Kind.ShouldBe(WpValueKind.Decimal);
        temperature.AsDecimal().ShouldBe(37.0m, "98.6 °F is exactly 37 °C in decimal arithmetic, whatever the culture");
    }

    [Fact]
    public async Task PIP_U_028_Future_source_time_replaced()
    {
        await _harness.StartAsync();
        var now = _harness.Time.GetUtcNow();
        var future = _harness.PositionOf(_harness.Door, "open") with { SourceTime = now.AddMinutes(10) };
        var plausible = _harness.PositionOf(_harness.Door, "closed") with { SourceTime = now.AddMinutes(4) };

        (await _harness.IngestAsync(future, plausible)).Accepted.ShouldBe(2);

        var records = _harness.Records();
        records[0].Envelope.Ts.ShouldBe(now);
        records[0].Envelope.Src.ShouldBe(now, "a source time more than Ingest:MaxFutureSkew ahead is replaced by the received time");
        records[1].Envelope.Src.ShouldBe(now.AddMinutes(4));
        var hint = _state.Drifts.ShouldHaveSingleItem();
        hint.ShouldBe(new ClockDriftHint(_harness.Door, null, _harness.Connection, now.AddMinutes(10), now));
    }

    [Fact]
    public async Task PIP_U_029_Unchanged_property_touch_only()
    {
        await _harness.StartAsync();
        var first = await _harness.IngestAsync(_harness.PositionOf(_harness.Door, "open"));
        _harness.Time.Advance(TimeSpan.FromSeconds(3));
        var second = await _harness.IngestAsync(_harness.PositionOf(_harness.Door, "open"));

        first.Counts().ShouldBe((1, 0, 0, 0));
        second.Counts().ShouldBe((0, 1, 0, 0));
        _harness.Records().Count.ShouldBe(1);
        _state.Records.Count.ShouldBe(1);
        _state.Touches.ShouldHaveSingleItem().ShouldBe(new TouchInfo(_harness.Door, null, IngestHarness.Position, _harness.Time.GetUtcNow(), null));
    }

    [Fact]
    public async Task PIP_U_030_Quality_change_counts_as_change()
    {
        await _harness.StartAsync();
        await _harness.IngestAsync(_harness.PositionOf(_harness.Door, "open"));
        var result = await _harness.IngestAsync(_harness.PositionOf(_harness.Door, "open", Quality.Uncertain));

        result.Accepted.ShouldBe(1);
        var records = _harness.Records();
        records.Count.ShouldBe(2);
        _harness.PropertyBody(records[1]).Quality.ShouldBe(Quality.Uncertain);
        _harness.PropertyBody(records[1]).Value.ShouldBe(WpValue.Enum("open"));
        _state.Touches.ShouldBeEmpty();
    }

    [Fact]
    public async Task PIP_U_031_Prev_value_included()
    {
        await _harness.StartAsync();

        // Both in one batch, then a third alone: prev follows the previous record either way.
        await _harness.IngestAsync(_harness.PositionOf(_harness.Door, "closed"), _harness.PositionOf(_harness.Door, "open"));
        await _harness.IngestAsync(_harness.PositionOf(_harness.Door, "forced"));

        var bodies = _harness.Records().Select(_harness.PropertyBody).ToList();
        bodies[0].Prev.ShouldBeNull();
        bodies[1].Prev.ShouldBe(WpValue.Enum("closed"));
        bodies[2].Prev.ShouldBe(WpValue.Enum("open"));
    }

    [Fact]
    public async Task PIP_U_032_Events_not_deduplicated_by_value()
    {
        await _harness.StartAsync();
        var first = _harness.Event(_harness.Door);
        var second = _harness.Event(_harness.Door);

        (await _harness.IngestAsync(first, second)).Accepted.ShouldBe(2);

        var records = _harness.Records();
        records.Select(r => r.Envelope.Id).ShouldBe([first.Id, second.Id]);
        records.ShouldAllBe(r => r.Envelope.Kind == RecordKinds.Event);
        records[0].BodyAs<EventRecordBody>(_harness.Log.Registry).Severity.ShouldBe(Severity.Alarm, "the severity comes from the type");

        // The same observation sent again (same id) is one record: the log deduplicates by id.
        (await _harness.IngestAsync(second)).Accepted.ShouldBe(1);
        _harness.Records().Count.ShouldBe(2);
    }

    [Fact]
    public async Task PIP_U_033_Health_unchanged_touch()
    {
        await _harness.StartAsync();
        var first = await _harness.IngestAsync(_harness.Health(_harness.Door, Connectivity.Degraded, "tamper", "batteryLow"));

        // The same state, the faults in another order.
        var second = await _harness.IngestAsync(_harness.Health(_harness.Door, Connectivity.Degraded, "batteryLow", "tamper"));
        var unknownFault = await _harness.IngestAsync(_harness.Health(_harness.Door, Connectivity.Degraded, "onFire"));
        var changed = await _harness.IngestAsync(_harness.Health(_harness.Door, Connectivity.Online));

        first.Accepted.ShouldBe(1);
        second.Counts().ShouldBe((0, 1, 0, 0));
        unknownFault.Rejected.Single().Error.Code.Value.ShouldBe("model.value.enum_unknown");
        changed.Accepted.ShouldBe(1);
        var records = _harness.Records();
        records.Count.ShouldBe(2);
        records[0].BodyAs<HealthRecordBody>(_harness.Log.Registry).Faults.ShouldBe(["batteryLow", "tamper"]);
        _state.Touches.ShouldHaveSingleItem().Property.ShouldBeNull();
    }
}

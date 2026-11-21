using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Tests.Fakes;

namespace Watchpoint.State.Tests.Support;

/// <summary>Builds synthetic <see cref="LogRecord"/>s for state module unit tests.</summary>
internal sealed class RecordBuilders(TestRecordTypeRegistry registry, TimeProvider time)
{
    private long _sequence;

    public LogRecord Property(DeviceId? device, ServerId? server, PropertyPath path, WpValue value, Quality quality = Quality.Good, DateTimeOffset? at = null, DateTimeOffset? source = null, CommandId? command = null)
    {
        var ts = at ?? time.GetUtcNow();
        var body = registry.SerializeBody(RecordKinds.Property, new PropertyRecordBody(path.Value, value, quality, null));
        var partition = Partitioning.Of(device?.Value ?? server!.Value.Value);
        var envelope = new LogEnvelope(Guid.NewGuid(), RecordKinds.Property, ts, source, partition, device, server, RecordOrigin.System, new RecordCorrelation(Command: command), body);
        return new LogRecord(++_sequence, envelope);
    }

    public LogRecord Event(DeviceId? device, ServerId? server, EventPath path, Severity severity, DateTimeOffset? at = null)
    {
        var ts = at ?? time.GetUtcNow();
        var body = registry.SerializeBody(RecordKinds.Event, new EventRecordBody(path.Value, new Dictionary<string, WpValue>(), severity));
        var partition = Partitioning.Of(device?.Value ?? server!.Value.Value);
        var envelope = new LogEnvelope(Guid.NewGuid(), RecordKinds.Event, ts, null, partition, device, server, RecordOrigin.System, RecordCorrelation.None, body);
        return new LogRecord(++_sequence, envelope);
    }

    public LogRecord Health(DeviceId? device, ServerId? server, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null, DateTimeOffset? at = null)
    {
        var ts = at ?? time.GetUtcNow();
        var body = registry.SerializeBody(RecordKinds.Health, new HealthRecordBody(connectivity, faults ?? [], reason));
        var partition = Partitioning.Of(device?.Value ?? server!.Value.Value);
        var envelope = new LogEnvelope(Guid.NewGuid(), RecordKinds.Health, ts, null, partition, device, server, RecordOrigin.System, RecordCorrelation.None, body);
        return new LogRecord(++_sequence, envelope);
    }
}

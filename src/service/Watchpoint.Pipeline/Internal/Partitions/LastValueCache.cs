using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Partitions;

/// <summary>
/// The last appended value of every property and the health of every target of one partition (spec PIP §7.2). Holds
/// exactly what the log holds: it is written only for records that are durable (or replayed), and only by the
/// partition's worker, so it needs no synchronisation.
/// </summary>
internal sealed class LastValueCache : ILastValueSink
{
    private readonly Dictionary<(Guid Target, PropertyPath Property), (WpValue Value, Quality Quality)> _properties = [];
    private readonly Dictionary<Guid, HealthRecordBody> _health = [];

    public int Count => _properties.Count + _health.Count;

    public bool TryGetProperty(Guid target, PropertyPath property, out WpValue value, out Quality quality)
    {
        if (_properties.TryGetValue((target, property), out var entry))
        {
            (value, quality) = entry;
            return true;
        }

        value = default;
        quality = default;
        return false;
    }

    public HealthRecordBody? GetHealth(Guid target) => _health.GetValueOrDefault(target);

    public void SetProperty(Guid target, PropertyPath path, WpValue value, Quality quality) =>
        _properties[(target, path)] = (value, quality);

    public void SetHealth(Guid target, HealthRecordBody health)
    {
        ArgumentNullException.ThrowIfNull(health);
        _health[target] = health;
    }

    /// <summary>Applies a record read from the log (recovery replay).</summary>
    public void Apply(LogRecord record, ILogRecordTypeRegistry registry)
    {
        var envelope = record.Envelope;
        if ((envelope.Dev?.Value ?? envelope.Srv?.Value) is not { } target || !registry.CanRead(record))
        {
            return;
        }

        switch (envelope.Kind)
        {
            case RecordKinds.Property:
                var body = record.BodyAs<PropertyRecordBody>(registry);
                var path = PropertyPath.Parse(body.Path);
                if (path.IsSuccess)
                {
                    SetProperty(target, path.Value, body.Value, body.Quality);
                }

                break;
            case RecordKinds.Health:
                SetHealth(target, record.BodyAs<HealthRecordBody>(registry));
                break;
        }
    }

    /// <summary>Whether two health states are the same state (faults are compared in order: the validator sorts them).</summary>
    public static bool SameHealth(HealthRecordBody a, HealthRecordBody b) =>
        a.Connectivity == b.Connectivity
        && string.Equals(a.Reason, b.Reason, StringComparison.Ordinal)
        && a.Faults.SequenceEqual(b.Faults, StringComparer.Ordinal);
}

using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>The visible set of a user for one permission, backed by a bitset (spec RGT §6).</summary>
internal sealed class VisibleScope : IVisibleScope
{
    private readonly ScopeIndex? _index;
    private readonly Bits? _bits;
    private readonly HashSet<LocationId>? _locations;
    private IReadOnlySet<DeviceId>? _devices;

    public VisibleScope(ScopeIndex index, Bits bits, HashSet<LocationId> locations, DateTimeOffset validUntil)
    {
        _index = index;
        _bits = bits;
        _locations = locations;
        ValidUntil = validUntil;
    }

    private VisibleScope(DateTimeOffset validUntil)
    {
        Everything = true;
        ValidUntil = validUntil;
    }

    public bool Everything { get; }

    public DateTimeOffset ValidUntil { get; }

    public IReadOnlySet<DeviceId>? Devices => Everything ? null : _devices ??= MaterializeDevices();

    public IReadOnlySet<LocationId>? Locations => Everything ? null : _locations;

    internal Bits? Bits => _bits;

    public static VisibleScope All(DateTimeOffset validUntil) => new(validUntil);

    public static VisibleScope Nothing(ScopeIndex index, DateTimeOffset validUntil) => new(index, new Bits(index.Count), [], validUntil);

    public bool ContainsDevice(DeviceId id) =>
        Everything || (_index!.TryIndexOf(id, out var i) && _bits![i]);

    public bool ContainsLocation(LocationId id) => Everything || _locations!.Contains(id);

    private HashSet<DeviceId> MaterializeDevices() => [.. _bits!.Indexes().Select(_index!.DeviceAt)];
}

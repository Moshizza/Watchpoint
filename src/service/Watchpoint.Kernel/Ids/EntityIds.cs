// All UUID-based id types of Watchpoint (spec K §4.1). Every type has exactly the same shape:
// keep them identical when editing (Architecture/specs/K-kernel.md §4.1).
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Watchpoint.Kernel.Ids;

/// <summary>Identifier of a site (prefix <c>site</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<SiteId>))]
[DebuggerDisplay("site:{Value}")]
public readonly record struct SiteId : IEntityId<SiteId>
{
    private SiteId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "site";

    /// <inheritdoc />
    public static SiteId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static SiteId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out SiteId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(SiteId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(SiteId left, SiteId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(SiteId left, SiteId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(SiteId left, SiteId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(SiteId left, SiteId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a cluster node (prefix <c>node</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<NodeId>))]
[DebuggerDisplay("node:{Value}")]
public readonly record struct NodeId : IEntityId<NodeId>
{
    private NodeId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "node";

    /// <inheritdoc />
    public static NodeId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static NodeId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out NodeId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(NodeId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(NodeId left, NodeId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(NodeId left, NodeId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(NodeId left, NodeId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(NodeId left, NodeId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a server (a connection to a third-party system) (prefix <c>srv</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ServerId>))]
[DebuggerDisplay("srv:{Value}")]
public readonly record struct ServerId : IEntityId<ServerId>
{
    private ServerId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "srv";

    /// <inheritdoc />
    public static ServerId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ServerId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ServerId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ServerId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ServerId left, ServerId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ServerId left, ServerId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ServerId left, ServerId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ServerId left, ServerId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a device (prefix <c>dev</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DeviceId>))]
[DebuggerDisplay("dev:{Value}")]
public readonly record struct DeviceId : IEntityId<DeviceId>
{
    private DeviceId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "dev";

    /// <inheritdoc />
    public static DeviceId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static DeviceId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out DeviceId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(DeviceId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(DeviceId left, DeviceId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(DeviceId left, DeviceId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(DeviceId left, DeviceId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(DeviceId left, DeviceId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a location (prefix <c>loc</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<LocationId>))]
[DebuggerDisplay("loc:{Value}")]
public readonly record struct LocationId : IEntityId<LocationId>
{
    private LocationId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "loc";

    /// <inheritdoc />
    public static LocationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static LocationId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out LocationId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(LocationId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(LocationId left, LocationId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(LocationId left, LocationId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(LocationId left, LocationId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(LocationId left, LocationId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a tag (prefix <c>tag</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<TagId>))]
[DebuggerDisplay("tag:{Value}")]
public readonly record struct TagId : IEntityId<TagId>
{
    private TagId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "tag";

    /// <inheritdoc />
    public static TagId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static TagId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out TagId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(TagId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(TagId left, TagId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(TagId left, TagId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(TagId left, TagId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(TagId left, TagId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a tag family (prefix <c>tagf</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<TagFamilyId>))]
[DebuggerDisplay("tagf:{Value}")]
public readonly record struct TagFamilyId : IEntityId<TagFamilyId>
{
    private TagFamilyId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "tagf";

    /// <inheritdoc />
    public static TagFamilyId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static TagFamilyId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out TagFamilyId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(TagFamilyId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(TagFamilyId left, TagFamilyId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(TagFamilyId left, TagFamilyId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(TagFamilyId left, TagFamilyId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(TagFamilyId left, TagFamilyId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a link (prefix <c>lnk</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<LinkId>))]
[DebuggerDisplay("lnk:{Value}")]
public readonly record struct LinkId : IEntityId<LinkId>
{
    private LinkId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "lnk";

    /// <inheritdoc />
    public static LinkId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static LinkId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out LinkId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(LinkId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(LinkId left, LinkId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(LinkId left, LinkId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(LinkId left, LinkId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(LinkId left, LinkId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a connection (prefix <c>conn</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ConnectionId>))]
[DebuggerDisplay("conn:{Value}")]
public readonly record struct ConnectionId : IEntityId<ConnectionId>
{
    private ConnectionId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "conn";

    /// <inheritdoc />
    public static ConnectionId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ConnectionId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ConnectionId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ConnectionId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ConnectionId left, ConnectionId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ConnectionId left, ConnectionId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ConnectionId left, ConnectionId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ConnectionId left, ConnectionId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a integration host (prefix <c>host</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<HostId>))]
[DebuggerDisplay("host:{Value}")]
public readonly record struct HostId : IEntityId<HostId>
{
    private HostId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "host";

    /// <inheritdoc />
    public static HostId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static HostId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out HostId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(HostId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(HostId left, HostId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(HostId left, HostId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(HostId left, HostId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(HostId left, HostId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a user (prefix <c>usr</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<UserId>))]
[DebuggerDisplay("usr:{Value}")]
public readonly record struct UserId : IEntityId<UserId>
{
    private UserId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "usr";

    /// <inheritdoc />
    public static UserId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static UserId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out UserId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(UserId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(UserId left, UserId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(UserId left, UserId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(UserId left, UserId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(UserId left, UserId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a user group (prefix <c>ugrp</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<UserGroupId>))]
[DebuggerDisplay("ugrp:{Value}")]
public readonly record struct UserGroupId : IEntityId<UserGroupId>
{
    private UserGroupId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "ugrp";

    /// <inheritdoc />
    public static UserGroupId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static UserGroupId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out UserGroupId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(UserGroupId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(UserGroupId left, UserGroupId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(UserGroupId left, UserGroupId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(UserGroupId left, UserGroupId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(UserGroupId left, UserGroupId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a session (prefix <c>ses</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<SessionId>))]
[DebuggerDisplay("ses:{Value}")]
public readonly record struct SessionId : IEntityId<SessionId>
{
    private SessionId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "ses";

    /// <inheritdoc />
    public static SessionId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static SessionId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out SessionId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(SessionId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(SessionId left, SessionId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(SessionId left, SessionId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(SessionId left, SessionId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(SessionId left, SessionId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a right grant (prefix <c>grant</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<RightGrantId>))]
[DebuggerDisplay("grant:{Value}")]
public readonly record struct RightGrantId : IEntityId<RightGrantId>
{
    private RightGrantId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "grant";

    /// <inheritdoc />
    public static RightGrantId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static RightGrantId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out RightGrantId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(RightGrantId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(RightGrantId left, RightGrantId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(RightGrantId left, RightGrantId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(RightGrantId left, RightGrantId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(RightGrantId left, RightGrantId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a draft (prefix <c>draft</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<DraftId>))]
[DebuggerDisplay("draft:{Value}")]
public readonly record struct DraftId : IEntityId<DraftId>
{
    private DraftId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "draft";

    /// <inheritdoc />
    public static DraftId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static DraftId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out DraftId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(DraftId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(DraftId left, DraftId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(DraftId left, DraftId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(DraftId left, DraftId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(DraftId left, DraftId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a change set (prefix <c>chg</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ChangeSetId>))]
[DebuggerDisplay("chg:{Value}")]
public readonly record struct ChangeSetId : IEntityId<ChangeSetId>
{
    private ChangeSetId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "chg";

    /// <inheritdoc />
    public static ChangeSetId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ChangeSetId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ChangeSetId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ChangeSetId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ChangeSetId left, ChangeSetId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ChangeSetId left, ChangeSetId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ChangeSetId left, ChangeSetId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ChangeSetId left, ChangeSetId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a custom field (prefix <c>cf</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CustomFieldId>))]
[DebuggerDisplay("cf:{Value}")]
public readonly record struct CustomFieldId : IEntityId<CustomFieldId>
{
    private CustomFieldId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "cf";

    /// <inheritdoc />
    public static CustomFieldId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static CustomFieldId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out CustomFieldId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(CustomFieldId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(CustomFieldId left, CustomFieldId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(CustomFieldId left, CustomFieldId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(CustomFieldId left, CustomFieldId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(CustomFieldId left, CustomFieldId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a holiday set (prefix <c>hol</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<HolidaySetId>))]
[DebuggerDisplay("hol:{Value}")]
public readonly record struct HolidaySetId : IEntityId<HolidaySetId>
{
    private HolidaySetId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "hol";

    /// <inheritdoc />
    public static HolidaySetId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static HolidaySetId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out HolidaySetId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(HolidaySetId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(HolidaySetId left, HolidaySetId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(HolidaySetId left, HolidaySetId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(HolidaySetId left, HolidaySetId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(HolidaySetId left, HolidaySetId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a workstation (prefix <c>wks</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<WorkstationId>))]
[DebuggerDisplay("wks:{Value}")]
public readonly record struct WorkstationId : IEntityId<WorkstationId>
{
    private WorkstationId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "wks";

    /// <inheritdoc />
    public static WorkstationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static WorkstationId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out WorkstationId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(WorkstationId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(WorkstationId left, WorkstationId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(WorkstationId left, WorkstationId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(WorkstationId left, WorkstationId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(WorkstationId left, WorkstationId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a alarm (prefix <c>alm</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AlarmId>))]
[DebuggerDisplay("alm:{Value}")]
public readonly record struct AlarmId : IEntityId<AlarmId>
{
    private AlarmId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "alm";

    /// <inheritdoc />
    public static AlarmId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static AlarmId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out AlarmId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(AlarmId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(AlarmId left, AlarmId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(AlarmId left, AlarmId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(AlarmId left, AlarmId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(AlarmId left, AlarmId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a alarm rule (prefix <c>arule</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AlarmRuleId>))]
[DebuggerDisplay("arule:{Value}")]
public readonly record struct AlarmRuleId : IEntityId<AlarmRuleId>
{
    private AlarmRuleId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "arule";

    /// <inheritdoc />
    public static AlarmRuleId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static AlarmRuleId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out AlarmRuleId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(AlarmRuleId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(AlarmRuleId left, AlarmRuleId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(AlarmRuleId left, AlarmRuleId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(AlarmRuleId left, AlarmRuleId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(AlarmRuleId left, AlarmRuleId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a procedure (prefix <c>proc</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ProcedureId>))]
[DebuggerDisplay("proc:{Value}")]
public readonly record struct ProcedureId : IEntityId<ProcedureId>
{
    private ProcedureId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "proc";

    /// <inheritdoc />
    public static ProcedureId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ProcedureId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ProcedureId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ProcedureId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ProcedureId left, ProcedureId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ProcedureId left, ProcedureId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ProcedureId left, ProcedureId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ProcedureId left, ProcedureId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a event record (prefix <c>evt</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<EventRecordId>))]
[DebuggerDisplay("evt:{Value}")]
public readonly record struct EventRecordId : IEntityId<EventRecordId>
{
    private EventRecordId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "evt";

    /// <inheritdoc />
    public static EventRecordId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static EventRecordId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out EventRecordId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(EventRecordId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(EventRecordId left, EventRecordId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(EventRecordId left, EventRecordId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(EventRecordId left, EventRecordId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(EventRecordId left, EventRecordId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a command (prefix <c>cmd</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CommandId>))]
[DebuggerDisplay("cmd:{Value}")]
public readonly record struct CommandId : IEntityId<CommandId>
{
    private CommandId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "cmd";

    /// <inheritdoc />
    public static CommandId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static CommandId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out CommandId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(CommandId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(CommandId left, CommandId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(CommandId left, CommandId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(CommandId left, CommandId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(CommandId left, CommandId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a calendar (prefix <c>cal</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<CalendarId>))]
[DebuggerDisplay("cal:{Value}")]
public readonly record struct CalendarId : IEntityId<CalendarId>
{
    private CalendarId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "cal";

    /// <inheritdoc />
    public static CalendarId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static CalendarId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out CalendarId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(CalendarId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(CalendarId left, CalendarId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(CalendarId left, CalendarId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(CalendarId left, CalendarId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(CalendarId left, CalendarId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a synoptic (prefix <c>syn</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<SynopticId>))]
[DebuggerDisplay("syn:{Value}")]
public readonly record struct SynopticId : IEntityId<SynopticId>
{
    private SynopticId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "syn";

    /// <inheritdoc />
    public static SynopticId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static SynopticId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out SynopticId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(SynopticId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(SynopticId left, SynopticId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(SynopticId left, SynopticId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(SynopticId left, SynopticId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(SynopticId left, SynopticId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a automation (prefix <c>auto</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AutomationId>))]
[DebuggerDisplay("auto:{Value}")]
public readonly record struct AutomationId : IEntityId<AutomationId>
{
    private AutomationId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "auto";

    /// <inheritdoc />
    public static AutomationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static AutomationId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out AutomationId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(AutomationId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(AutomationId left, AutomationId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(AutomationId left, AutomationId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(AutomationId left, AutomationId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(AutomationId left, AutomationId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a automation run (prefix <c>run</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<AutomationRunId>))]
[DebuggerDisplay("run:{Value}")]
public readonly record struct AutomationRunId : IEntityId<AutomationRunId>
{
    private AutomationRunId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "run";

    /// <inheritdoc />
    public static AutomationRunId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static AutomationRunId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out AutomationRunId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(AutomationRunId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(AutomationRunId left, AutomationRunId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(AutomationRunId left, AutomationRunId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(AutomationRunId left, AutomationRunId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(AutomationRunId left, AutomationRunId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a script (prefix <c>scr</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ScriptId>))]
[DebuggerDisplay("scr:{Value}")]
public readonly record struct ScriptId : IEntityId<ScriptId>
{
    private ScriptId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "scr";

    /// <inheritdoc />
    public static ScriptId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ScriptId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ScriptId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ScriptId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ScriptId left, ScriptId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ScriptId left, ScriptId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ScriptId left, ScriptId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ScriptId left, ScriptId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a notification (prefix <c>ntf</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<NotificationId>))]
[DebuggerDisplay("ntf:{Value}")]
public readonly record struct NotificationId : IEntityId<NotificationId>
{
    private NotificationId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "ntf";

    /// <inheritdoc />
    public static NotificationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static NotificationId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out NotificationId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(NotificationId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(NotificationId left, NotificationId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(NotificationId left, NotificationId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(NotificationId left, NotificationId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(NotificationId left, NotificationId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a report (prefix <c>rpt</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ReportId>))]
[DebuggerDisplay("rpt:{Value}")]
public readonly record struct ReportId : IEntityId<ReportId>
{
    private ReportId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "rpt";

    /// <inheritdoc />
    public static ReportId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ReportId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ReportId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ReportId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ReportId left, ReportId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ReportId left, ReportId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ReportId left, ReportId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ReportId left, ReportId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a file (prefix <c>file</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<FileId>))]
[DebuggerDisplay("file:{Value}")]
public readonly record struct FileId : IEntityId<FileId>
{
    private FileId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "file";

    /// <inheritdoc />
    public static FileId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static FileId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out FileId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(FileId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(FileId left, FileId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(FileId left, FileId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(FileId left, FileId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(FileId left, FileId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a secret (prefix <c>sec</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<SecretId>))]
[DebuggerDisplay("sec:{Value}")]
public readonly record struct SecretId : IEntityId<SecretId>
{
    private SecretId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "sec";

    /// <inheritdoc />
    public static SecretId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static SecretId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out SecretId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(SecretId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(SecretId left, SecretId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(SecretId left, SecretId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(SecretId left, SecretId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(SecretId left, SecretId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a licence (prefix <c>lic</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<LicenceId>))]
[DebuggerDisplay("lic:{Value}")]
public readonly record struct LicenceId : IEntityId<LicenceId>
{
    private LicenceId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "lic";

    /// <inheritdoc />
    public static LicenceId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static LicenceId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out LicenceId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(LicenceId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(LicenceId left, LicenceId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(LicenceId left, LicenceId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(LicenceId left, LicenceId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(LicenceId left, LicenceId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a federation link (prefix <c>fed</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<FederationLinkId>))]
[DebuggerDisplay("fed:{Value}")]
public readonly record struct FederationLinkId : IEntityId<FederationLinkId>
{
    private FederationLinkId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "fed";

    /// <inheritdoc />
    public static FederationLinkId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static FederationLinkId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out FederationLinkId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(FederationLinkId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(FederationLinkId left, FederationLinkId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(FederationLinkId left, FederationLinkId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(FederationLinkId left, FederationLinkId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(FederationLinkId left, FederationLinkId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a observation (prefix <c>obs</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<ObservationId>))]
[DebuggerDisplay("obs:{Value}")]
public readonly record struct ObservationId : IEntityId<ObservationId>
{
    private ObservationId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "obs";

    /// <inheritdoc />
    public static ObservationId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static ObservationId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out ObservationId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(ObservationId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(ObservationId left, ObservationId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(ObservationId left, ObservationId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(ObservationId left, ObservationId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(ObservationId left, ObservationId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a job (prefix <c>job</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<JobId>))]
[DebuggerDisplay("job:{Value}")]
public readonly record struct JobId : IEntityId<JobId>
{
    private JobId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "job";

    /// <inheritdoc />
    public static JobId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static JobId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out JobId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(JobId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(JobId left, JobId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(JobId left, JobId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(JobId left, JobId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(JobId left, JobId right) => left.CompareTo(right) >= 0;
}

/// <summary>Identifier of a incident (prefix <c>inc</c>).</summary>
[JsonConverter(typeof(EntityIdJsonConverter<IncidentId>))]
[DebuggerDisplay("inc:{Value}")]
public readonly record struct IncidentId : IEntityId<IncidentId>
{
    private IncidentId(Guid value) => Value = value;

    /// <inheritdoc />
    public Guid Value { get; }

    /// <inheritdoc />
    public static string Prefix => "inc";

    /// <inheritdoc />
    public static IncidentId New() => new(Guid.CreateVersion7());

    /// <inheritdoc />
    public static IncidentId From(Guid value) => new(EntityIdHelper.EnsureNotEmpty(value));

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, out IncidentId id)
    {
        var ok = EntityIdHelper.TryParse(s, out var value);
        id = ok ? new(value) : default;
        return ok;
    }

    /// <summary>Compares ids by UUID value; version 7 ids sort by creation time (millisecond precision).</summary>
    /// <param name="other">The id to compare with.</param>
    /// <returns>A negative number, zero or a positive number.</returns>
    public int CompareTo(IncidentId other) => Value.CompareTo(other.Value);

    /// <summary>Returns the UUID in lowercase <c>D</c> format (36 characters).</summary>
    /// <returns>The formatted id.</returns>
    public override string ToString() => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => EntityIdHelper.Format(Value);

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider) =>
        EntityIdHelper.TryFormat(Value, destination, out charsWritten);

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before <paramref name="right"/>.</returns>
    public static bool operator <(IncidentId left, IncidentId right) => left.CompareTo(right) < 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts before or equals <paramref name="right"/>.</returns>
    public static bool operator <=(IncidentId left, IncidentId right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after <paramref name="right"/>.</returns>
    public static bool operator >(IncidentId left, IncidentId right) => left.CompareTo(right) > 0;

    /// <summary>Compares two ids.</summary>
    /// <param name="left">The first id.</param>
    /// <param name="right">The second id.</param>
    /// <returns><see langword="true"/> when <paramref name="left"/> sorts after or equals <paramref name="right"/>.</returns>
    public static bool operator >=(IncidentId left, IncidentId right) => left.CompareTo(right) >= 0;
}

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;

namespace Watchpoint.Kernel.Values;

/// <summary>
/// The single representation of a property value, command argument or event payload field (spec K §10).
/// Immutable; <see langword="default"/> is <see cref="Null"/>. Equality: same kind and equal value (decimals
/// numerically, lists element-wise, objects by canonical JSON). JSON form: <c>{"k":"&lt;kind&gt;","v":…}</c>
/// (<see cref="WpValueJsonConverter"/>); the compact form is read with <see cref="ReadCompact"/>.
/// </summary>
[JsonConverter(typeof(WpValueJsonConverter))]
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Factory names fixed by spec K §10.")]
[SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Factory names fixed by spec K §10.")]
public readonly partial struct WpValue : IEquatable<WpValue>
{
    /// <summary>The maximum size of a string value when read from JSON (UTF-8 bytes).</summary>
    public const int MaxStringBytes = 16 * 1024;

    /// <summary>The maximum number of items of a list value when read from JSON.</summary>
    public const int MaxListItems = 1000;

    /// <summary>The maximum size of an object value when read from JSON (canonical JSON, UTF-8 bytes).</summary>
    public const int MaxObjectBytes = 16 * 1024;

    /// <summary>The maximum nesting depth when read from JSON (a scalar is depth 1; lists and objects add one level).</summary>
    public const int MaxDepth = 8;

    /// <summary>The pattern of enumeration codes.</summary>
    public const string EnumPattern = "^[a-z][a-zA-Z0-9]*$";

    /// <summary>The null value.</summary>
    public static readonly WpValue Null;

    private readonly long _bits;
    private readonly decimal _decimal;
    private readonly object? _ref;

    private WpValue(WpValueKind kind, long bits = 0, decimal number = 0m, object? reference = null)
    {
        Kind = kind;
        _bits = bits;
        _decimal = number;
        _ref = reference;
    }

    /// <summary>Gets the kind.</summary>
    public WpValueKind Kind { get; }

    /// <summary>Gets a value indicating whether this is <see cref="Null"/>.</summary>
    public bool IsNull => Kind == WpValueKind.Null;

    /// <summary>Creates a boolean value.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The value.</returns>
    public static WpValue Bool(bool v) => new(WpValueKind.Bool, v ? 1 : 0);

    /// <summary>Creates an integer value.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The value.</returns>
    public static WpValue Int(long v) => new(WpValueKind.Int, v);

    /// <summary>Creates a decimal value.</summary>
    /// <param name="v">The value.</param>
    /// <returns>The value.</returns>
    public static WpValue Decimal(decimal v) => new(WpValueKind.Decimal, number: v);

    /// <summary>Creates an enumeration value.</summary>
    /// <param name="code">The member code, pattern <see cref="EnumPattern"/>, e.g. <c>open</c>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException">The code does not match the pattern.</exception>
    public static WpValue Enum(string code) =>
        code is not null && EnumRegex().IsMatch(code)
            ? new(WpValueKind.Enum, reference: code)
            : throw new ArgumentException($"'{code}' is not a valid enumeration code (pattern {EnumPattern}).", nameof(code));

    /// <summary>Creates a text value.</summary>
    /// <param name="v">The text.</param>
    /// <returns>The value.</returns>
    public static WpValue String(string v) => new(WpValueKind.String, reference: v ?? throw new ArgumentNullException(nameof(v)));

    /// <summary>Creates a timestamp, converted to UTC and truncated to milliseconds.</summary>
    /// <param name="utc">The instant (any offset).</param>
    /// <returns>The value.</returns>
    public static WpValue Timestamp(DateTimeOffset utc)
    {
        var ticks = utc.UtcTicks;
        return new(WpValueKind.Timestamp, ticks - (ticks % TimeSpan.TicksPerMillisecond));
    }

    /// <summary>Creates a duration.</summary>
    /// <param name="v">The duration.</param>
    /// <returns>The value.</returns>
    public static WpValue Duration(TimeSpan v) => new(WpValueKind.Duration, v.Ticks);

    /// <summary>Creates a position.</summary>
    /// <param name="lat">Latitude in degrees, −90 to 90.</param>
    /// <param name="lon">Longitude in degrees, −180 to 180.</param>
    /// <param name="alt">Altitude in metres, or <see langword="null"/>.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is out of range or not finite.</exception>
    public static WpValue Geo(double lat, double lon, double? alt = null)
    {
        if (!double.IsFinite(lat) || lat is < -90 or > 90)
        {
            throw new ArgumentOutOfRangeException(nameof(lat), lat, "Latitude must be between -90 and 90.");
        }

        if (!double.IsFinite(lon) || lon is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(lon), lon, "Longitude must be between -180 and 180.");
        }

        if (alt is { } a && !double.IsFinite(a))
        {
            throw new ArgumentOutOfRangeException(nameof(alt), alt, "Altitude must be finite.");
        }

        return new(WpValueKind.Geo, reference: new GeoPoint(lat, lon, alt));
    }

    /// <summary>Creates a list (the items are copied).</summary>
    /// <param name="items">The items.</param>
    /// <returns>The value.</returns>
    public static WpValue List(IReadOnlyList<WpValue> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return new(WpValueKind.List, reference: Array.AsReadOnly(items.ToArray()));
    }

    /// <summary>Creates an object value from a JSON object (cloned, so the source document may be disposed).</summary>
    /// <param name="value">A JSON object.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a JSON object.</exception>
    public static WpValue Object(JsonElement value) =>
        value.ValueKind == JsonValueKind.Object
            ? new(WpValueKind.Object, reference: new ObjectValue(value.Clone()))
            : throw new ArgumentException($"Expected a JSON object, got {value.ValueKind}.", nameof(value));

    /// <summary>Gets the boolean.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Bool"/>.</exception>
    public bool AsBool() => TryAsBool(out var v) ? v : throw Mismatch(WpValueKind.Bool);

    /// <summary>Gets the integer.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Int"/>.</exception>
    public long AsInt() => TryAsInt(out var v) ? v : throw Mismatch(WpValueKind.Int);

    /// <summary>Gets the decimal.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Decimal"/>.</exception>
    public decimal AsDecimal() => TryAsDecimal(out var v) ? v : throw Mismatch(WpValueKind.Decimal);

    /// <summary>Gets the enumeration code.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Enum"/>.</exception>
    public string AsEnum() => TryAsEnum(out var v) ? v : throw Mismatch(WpValueKind.Enum);

    /// <summary>Gets the text.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.String"/>.</exception>
    public string AsString() => TryAsString(out var v) ? v : throw Mismatch(WpValueKind.String);

    /// <summary>Gets the timestamp (offset zero).</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Timestamp"/>.</exception>
    public DateTimeOffset AsTimestamp() => TryAsTimestamp(out var v) ? v : throw Mismatch(WpValueKind.Timestamp);

    /// <summary>Gets the duration.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Duration"/>.</exception>
    public TimeSpan AsDuration() => TryAsDuration(out var v) ? v : throw Mismatch(WpValueKind.Duration);

    /// <summary>Gets the position.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Geo"/>.</exception>
    public GeoPoint AsGeo() => TryAsGeo(out var v) ? v : throw Mismatch(WpValueKind.Geo);

    /// <summary>Gets the items.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.List"/>.</exception>
    public IReadOnlyList<WpValue> AsList() => TryAsList(out var v) ? v : throw Mismatch(WpValueKind.List);

    /// <summary>Gets the JSON object.</summary>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The kind is not <see cref="WpValueKind.Object"/>.</exception>
    public JsonElement AsObject() => TryAsObject(out var v) ? v : throw Mismatch(WpValueKind.Object);

    /// <summary>Gets the boolean when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Bool"/>.</returns>
    public bool TryAsBool(out bool value)
    {
        value = Kind == WpValueKind.Bool && _bits != 0;
        return Kind == WpValueKind.Bool;
    }

    /// <summary>Gets the integer when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Int"/>.</returns>
    public bool TryAsInt(out long value)
    {
        value = Kind == WpValueKind.Int ? _bits : 0;
        return Kind == WpValueKind.Int;
    }

    /// <summary>Gets the decimal when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Decimal"/>.</returns>
    public bool TryAsDecimal(out decimal value)
    {
        value = Kind == WpValueKind.Decimal ? _decimal : 0m;
        return Kind == WpValueKind.Decimal;
    }

    /// <summary>Gets the enumeration code when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Enum"/>.</returns>
    public bool TryAsEnum([NotNullWhen(true)] out string? value)
    {
        value = Kind == WpValueKind.Enum ? (string)_ref! : null;
        return value is not null;
    }

    /// <summary>Gets the text when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.String"/>.</returns>
    public bool TryAsString([NotNullWhen(true)] out string? value)
    {
        value = Kind == WpValueKind.String ? (string)_ref! : null;
        return value is not null;
    }

    /// <summary>Gets the timestamp when the kind matches.</summary>
    /// <param name="value">The value (offset zero).</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Timestamp"/>.</returns>
    public bool TryAsTimestamp(out DateTimeOffset value)
    {
        value = Kind == WpValueKind.Timestamp ? new DateTimeOffset(_bits, TimeSpan.Zero) : default;
        return Kind == WpValueKind.Timestamp;
    }

    /// <summary>Gets the duration when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Duration"/>.</returns>
    public bool TryAsDuration(out TimeSpan value)
    {
        value = Kind == WpValueKind.Duration ? TimeSpan.FromTicks(_bits) : default;
        return Kind == WpValueKind.Duration;
    }

    /// <summary>Gets the position when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Geo"/>.</returns>
    public bool TryAsGeo(out GeoPoint value)
    {
        value = Kind == WpValueKind.Geo ? (GeoPoint)_ref! : default;
        return Kind == WpValueKind.Geo;
    }

    /// <summary>Gets the items when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.List"/>.</returns>
    public bool TryAsList([NotNullWhen(true)] out IReadOnlyList<WpValue>? value)
    {
        value = Kind == WpValueKind.List ? (IReadOnlyList<WpValue>)_ref! : null;
        return value is not null;
    }

    /// <summary>Gets the JSON object when the kind matches.</summary>
    /// <param name="value">The value.</param>
    /// <returns><see langword="true"/> when the kind is <see cref="WpValueKind.Object"/>.</returns>
    public bool TryAsObject(out JsonElement value)
    {
        value = Kind == WpValueKind.Object ? ((ObjectValue)_ref!).Element : default;
        return Kind == WpValueKind.Object;
    }

    /// <summary>
    /// Reads the compact form (a plain JSON value without kind), accepted where the target kind is known, e.g. API
    /// input validated against a property definition. JSON <c>null</c> reads as <see cref="Null"/>. Decimals accept
    /// a number or a string; timestamps and durations accept their ISO 8601 strings; positions accept
    /// <c>[lat, lon]</c> or <c>[lat, lon, alt]</c>. Lists have no compact form (their items need a definition).
    /// Size and depth limits apply.
    /// </summary>
    /// <param name="json">The JSON value.</param>
    /// <param name="targetKind">The expected kind; <see langword="null"/> always fails.</param>
    /// <returns>The value, or <c>common.value.invalid_format</c> / <c>common.value.too_long</c>.</returns>
    public static Result<WpValue> ReadCompact(JsonElement json, WpValueKind? targetKind) =>
        targetKind is { } kind
            ? WpValueReader.ReadCompact(json, kind)
            : CommonErrors.InvalidFormat("a value with a known kind (full form {\"k\":…,\"v\":…})");

    /// <inheritdoc />
    public bool Equals(WpValue other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        return Kind switch
        {
            WpValueKind.Null => true,
            WpValueKind.Decimal => _decimal == other._decimal,
            WpValueKind.Enum or WpValueKind.String => string.Equals((string)_ref!, (string)other._ref!, StringComparison.Ordinal),
            WpValueKind.Geo => ((GeoPoint)_ref!).Equals((GeoPoint)other._ref!),
            WpValueKind.List => ((IReadOnlyList<WpValue>)_ref!).SequenceEqual((IReadOnlyList<WpValue>)other._ref!),
            WpValueKind.Object => string.Equals(((ObjectValue)_ref!).Canonical, ((ObjectValue)other._ref!).Canonical, StringComparison.Ordinal),
            _ => _bits == other._bits,
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WpValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        switch (Kind)
        {
            case WpValueKind.Null:
                return 0;
            case WpValueKind.Decimal:
                // Dividing by 1.000…0 removes trailing zeros, so 1.0 and 1.00 hash alike.
                return HashCode.Combine(Kind, _decimal / 1.0000000000000000000000000000m);
            case WpValueKind.Enum or WpValueKind.String:
                return HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode((string)_ref!));
            case WpValueKind.Geo:
                return HashCode.Combine(Kind, (GeoPoint)_ref!);
            case WpValueKind.List:
                var hash = new HashCode();
                hash.Add(Kind);
                foreach (var item in (IReadOnlyList<WpValue>)_ref!)
                {
                    hash.Add(item);
                }

                return hash.ToHashCode();
            case WpValueKind.Object:
                return HashCode.Combine(Kind, StringComparer.Ordinal.GetHashCode(((ObjectValue)_ref!).Canonical));
            default:
                return HashCode.Combine(Kind, _bits);
        }
    }

    /// <summary>Returns a short form for logs and debugging, e.g. <c>int:5</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() =>
        Kind switch
        {
            WpValueKind.Null => "null",
            WpValueKind.Bool => _bits != 0 ? "bool:true" : "bool:false",
            WpValueKind.Int => "int:" + _bits.ToString(CultureInfo.InvariantCulture),
            WpValueKind.Decimal => "dec:" + _decimal.ToString(CultureInfo.InvariantCulture),
            WpValueKind.Enum => "enum:" + (string)_ref!,
            WpValueKind.String => "str:" + (string)_ref!,
            WpValueKind.Timestamp => "ts:" + UtcDateTimeOffsetConverter.Format(AsTimestamp()),
            WpValueKind.Duration => "dur:" + Iso8601DurationConverter.Format(AsDuration()),
            WpValueKind.Geo => "geo:" + string.Create(CultureInfo.InvariantCulture, $"{AsGeo().Latitude},{AsGeo().Longitude}"),
            WpValueKind.List => "list:[" + string.Join(",", AsList()) + "]",
            _ => "obj:" + ((ObjectValue)_ref!).Canonical,
        };

    /// <summary>Compares two values.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(WpValue left, WpValue right) => left.Equals(right);

    /// <summary>Compares two values.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(WpValue left, WpValue right) => !left.Equals(right);

    /// <summary>Gets the canonical JSON of an object value (for size checks).</summary>
    internal string CanonicalObject => ((ObjectValue)_ref!).Canonical;

    private InvalidOperationException Mismatch(WpValueKind expected) => new($"The value is of kind {Kind}, not {expected}.");

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EnumRegex();

    private sealed class ObjectValue(JsonElement element)
    {
        public JsonElement Element { get; } = element;

        public string Canonical { get; } = CanonicalJson.Write(element);
    }
}

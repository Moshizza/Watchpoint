using System.Globalization;
using Watchpoint.Kernel.Values;

namespace Watchpoint.History.Internal.Writers;

/// <summary>The value columns of a property change (spec HIS §4): at most one is set.</summary>
/// <param name="Num">The numeric column.</param>
/// <param name="Text">The text column.</param>
/// <param name="Bool">The boolean column.</param>
/// <param name="Json">The JSON column.</param>
internal readonly record struct ValueColumns(decimal? Num, string? Text, bool? Bool, string? Json)
{
    /// <summary>
    /// Maps a value: Bool → <c>v_bool</c>; Int and Decimal → <c>v_num</c>; Enum and String → <c>v_text</c>; Timestamp → <c>v_text</c> (ISO 8601 UTC);
    /// Duration → <c>v_num</c> in seconds; Geo, List and Object → <c>v_json</c>; Null → no column.
    /// </summary>
    public static ValueColumns From(WpValue value) => value.Kind switch
    {
        WpValueKind.Bool => new(null, null, value.AsBool(), null),
        WpValueKind.Int => new(value.AsInt(), null, null, null),
        WpValueKind.Decimal => new(value.AsDecimal(), null, null, null),
        WpValueKind.Enum => new(null, value.AsEnum(), null, null),
        WpValueKind.String => new(null, value.AsString(), null, null),
        WpValueKind.Timestamp => new(null, value.AsTimestamp().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), null, null),
        WpValueKind.Duration => new((decimal)value.AsDuration().Ticks / TimeSpan.TicksPerSecond, null, null, null),
        WpValueKind.Geo or WpValueKind.List or WpValueKind.Object => new(null, null, null, HistoryJson.Serialize(value)),
        _ => default,
    };

    /// <summary>Whether two values are the same stored value (numbers by value, the rest by text).</summary>
    public bool SameAs(ValueColumns other) => Num == other.Num && Text == other.Text && Bool == other.Bool && Json == other.Json;

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Num}|{Text}|{Bool}|{Json}");
}

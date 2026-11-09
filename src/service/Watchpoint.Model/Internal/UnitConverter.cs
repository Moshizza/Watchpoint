using Watchpoint.Kernel.Results;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Model.Internal;

/// <summary>
/// Converts between compatible UCUM units for supported dimensions (spec MOD §5.3):
/// Temperature: Cel, [degF], K
/// Length: m, cm, mm, km
/// Energy: W.h, kW.h, MW.h, J, MJ
/// Power: W, kW, MW
/// Pressure: Pa, kPa, bar, mbar
/// Volume: L, m3
/// Flow: L/s, m3/h
/// Time: s, min, h
/// Ratio: %, 1
/// </summary>
internal static class UnitConverter
{
    private enum Dimension
    {
        Temperature,
        Length,
        Energy,
        Power,
        Pressure,
        Volume,
        Flow,
        Time,
        Ratio,
    }

    private sealed record UnitInfo(Dimension Dimension, decimal Factor, decimal Offset = 0m);

    private static readonly Dictionary<string, UnitInfo> _units = new(StringComparer.Ordinal)
    {
        // Length (base: m)
        ["m"] = new(Dimension.Length, 1m),
        ["cm"] = new(Dimension.Length, 0.01m),
        ["mm"] = new(Dimension.Length, 0.001m),
        ["km"] = new(Dimension.Length, 1000m),

        // Energy (base: W.h)
        ["W.h"] = new(Dimension.Energy, 1m),
        ["kW.h"] = new(Dimension.Energy, 1000m),
        ["MW.h"] = new(Dimension.Energy, 1000000m),
        ["J"] = new(Dimension.Energy, 1m / 3600m),
        ["MJ"] = new(Dimension.Energy, 1000000m / 3600m),

        // Power (base: W)
        ["W"] = new(Dimension.Power, 1m),
        ["kW"] = new(Dimension.Power, 1000m),
        ["MW"] = new(Dimension.Power, 1000000m),

        // Pressure (base: Pa)
        ["Pa"] = new(Dimension.Pressure, 1m),
        ["kPa"] = new(Dimension.Pressure, 1000m),
        ["bar"] = new(Dimension.Pressure, 100000m),
        ["mbar"] = new(Dimension.Pressure, 100m),

        // Volume (base: m3)
        ["m3"] = new(Dimension.Volume, 1m),
        ["L"] = new(Dimension.Volume, 0.001m),

        // Flow (base: m3/h)
        ["m3/h"] = new(Dimension.Flow, 1m),
        ["L/s"] = new(Dimension.Flow, 3.6m),

        // Time (base: s)
        ["s"] = new(Dimension.Time, 1m),
        ["min"] = new(Dimension.Time, 60m),
        ["h"] = new(Dimension.Time, 3600m),

        // Ratio (base: 1)
        ["1"] = new(Dimension.Ratio, 1m),
        ["%"] = new(Dimension.Ratio, 0.01m),
    };

    public static Result<decimal> Convert(decimal value, string fromUnit, string toUnit, string path = "")
    {
        if (string.Equals(fromUnit, toUnit, StringComparison.Ordinal))
        {
            return value;
        }

        // Temperature special case
        if (IsTemperature(fromUnit) && IsTemperature(toUnit))
        {
            return ConvertTemperature(value, fromUnit, toUnit);
        }

        if (!_units.TryGetValue(fromUnit, out var fromInfo) || !_units.TryGetValue(toUnit, out var toInfo) || fromInfo.Dimension != toInfo.Dimension)
        {
            return ModelErrors.UnitIncompatibleError(fromUnit, toUnit, path);
        }

        var inBase = value * fromInfo.Factor;
        var result = inBase / toInfo.Factor;
        return result;
    }

    private static bool IsTemperature(string unit) => unit is "Cel" or "[degF]" or "K";

    private static Result<decimal> ConvertTemperature(decimal value, string fromUnit, string toUnit)
    {
        decimal celsius = fromUnit switch
        {
            "Cel" => value,
            "[degF]" => (value - 32m) * 5m / 9m,
            "K" => value - 273.15m,
            _ => 0m,
        };

        decimal result = toUnit switch
        {
            "Cel" => celsius,
            "[degF]" => (celsius * 9m / 5m) + 32m,
            "K" => celsius + 273.15m,
            _ => 0m,
        };

        return result;
    }
}

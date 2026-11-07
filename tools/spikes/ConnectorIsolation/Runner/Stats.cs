using System.Diagnostics;
using System.Globalization;

namespace Watchpoint.Spikes.ConnectorIsolation.Runner;

/// <summary>Percentiles of Stopwatch-tick samples, in milliseconds.</summary>
internal sealed record Stats(double P50, double P99, double P999, double Max)
{
    public static Stats FromTicks(long[] ticks)
    {
        var sorted = (long[])ticks.Clone();
        Array.Sort(sorted);
        double Ms(long t) => t * 1000.0 / Stopwatch.Frequency;
        double At(double q) => Ms(sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))]);
        return new Stats(At(0.50), At(0.99), At(0.999), Ms(sorted[^1]));
    }

    public static string F(double value) => value.ToString(value >= 100 ? "N0" : value >= 10 ? "N1" : "N3", CultureInfo.InvariantCulture);

    public string Row => $"{F(P50)} | {F(P99)} | {F(P999)} | {F(Max)}";
}

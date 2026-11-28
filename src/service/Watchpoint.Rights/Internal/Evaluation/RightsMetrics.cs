using System.Diagnostics.Metrics;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>Metrics of the rights module (spec RGT §14).</summary>
internal static class RightsMetrics
{
    public static readonly Meter Meter = new("Watchpoint.Rights");

    public static readonly Histogram<double> AuthorizeDuration = Meter.CreateHistogram<double>("watchpoint.rights.authorize.duration", "us");

    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("watchpoint.rights.cache.hits");

    public static readonly Counter<long> ScopeRecompute = Meter.CreateCounter<long>("watchpoint.rights.scope.recompute");
}

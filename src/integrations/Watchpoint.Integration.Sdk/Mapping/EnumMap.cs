using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Sdk.Mapping;

/// <summary>
/// Maps vendor codes to Watchpoint enum values (INT §10).
/// Unknown codes are mapped to a declared fallback without throwing exceptions, and counted via metrics/logs.
/// </summary>
/// <typeparam name="TVendor">The vendor code type (e.g. string, int).</typeparam>
/// <typeparam name="TTarget">The target Watchpoint enum or model type.</typeparam>
public sealed class EnumMap<TVendor, TTarget> where TVendor : notnull
{
    private readonly Dictionary<TVendor, TTarget> _map;
    private readonly TTarget _fallback;
    private readonly IConnectorMetrics? _metrics;
    private readonly ILogger? _logger;
    private readonly string _name;
    private long _unknownCount;

    /// <summary>Gets the total count of unknown vendor code occurrences.</summary>
    public long UnknownCount => Interlocked.Read(ref _unknownCount);

    /// <summary>Initializes a new instance of <see cref="EnumMap{TVendor, TTarget}"/>.</summary>
    /// <param name="name">Name of this map for diagnostics.</param>
    /// <param name="mappings">Known mappings from vendor codes to target values.</param>
    /// <param name="fallback">Fallback value returned for unknown vendor codes.</param>
    /// <param name="metrics">Metrics recorder for unknown code count.</param>
    /// <param name="logger">Logger for debug reporting.</param>
    public EnumMap(
        string name,
        IReadOnlyDictionary<TVendor, TTarget> mappings,
        TTarget fallback,
        IConnectorMetrics? metrics = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mappings);

        _name = name;
        _map = new Dictionary<TVendor, TTarget>(mappings);
        _fallback = fallback;
        _metrics = metrics;
        _logger = logger;
    }

    /// <summary>Maps a vendor code to the target type, returning the fallback if unrecognised.</summary>
    /// <param name="vendorCode">The vendor code to map.</param>
    /// <returns>The mapped target value or fallback.</returns>
    public TTarget Map(TVendor vendorCode)
    {
        if (vendorCode is not null && _map.TryGetValue(vendorCode, out var target))
        {
            return target;
        }

        var count = Interlocked.Increment(ref _unknownCount);
        _metrics?.SetGauge($"enum_map_{_name}_unknown_total", count);
        if (_logger is not null && _logger.IsEnabled(LogLevel.Debug))
        {
            var vendorCodeStr = vendorCode?.ToString() ?? string.Empty;
            var fallbackStr = _fallback?.ToString() ?? string.Empty;
            SdkLog.UnknownVendorCode(_logger, _name, vendorCodeStr, fallbackStr);
        }

        return _fallback;
    }
}

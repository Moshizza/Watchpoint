using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Scheduling;

/// <summary>Log events for scheduling (spec SCH §8, event ids 2350–2399).</summary>
internal static partial class SchedulingLog
{
    [LoggerMessage(EventId = 2350, Level = LogLevel.Debug, Message = "Trigger registered: owner={Owner}, key={Key}, spec={Spec}")]
    public static partial void TriggerRegistered(ILogger logger, string owner, string key, string spec);

    [LoggerMessage(EventId = 2351, Level = LogLevel.Information, Message = "Catch-up trigger fired: owner={Owner}, key={Key}, scheduledAt={ScheduledAt}")]
    public static partial void CatchUpFired(ILogger logger, string owner, string key, DateTimeOffset scheduledAt);

    [LoggerMessage(EventId = 2352, Level = LogLevel.Warning, Message = "Clock jump backwards detected: delta={DeltaMs} ms. Recomputing trigger heap.")]
    public static partial void ClockJumpDetected(ILogger logger, double deltaMs);

    [LoggerMessage(EventId = 2353, Level = LogLevel.Information, Message = "Polar day or polar night at location ({Latitude}, {Longitude}) on date {Date}; no solar event scheduled.")]
    public static partial void PolarDayNight(ILogger logger, double latitude, double longitude, DateOnly date);

    [LoggerMessage(EventId = 2354, Level = LogLevel.Warning, Message = "Calendar key '{CalendarKey}' not found; returning inactive.")]
    public static partial void UnknownCalendarKey(ILogger logger, string calendarKey);

    [LoggerMessage(EventId = 2355, Level = LogLevel.Warning, Message = "ICS import contained unsupported element or property: '{Warning}'")]
    public static partial void IcsWarning(ILogger logger, string warning);
}

/// <summary>Metrics definitions for the scheduling module (spec SCH §8).</summary>
public static class SchedulingMetrics
{
    /// <summary>Meter name for scheduling metrics.</summary>
    public const string MeterName = "Watchpoint.Scheduling";

    private static readonly Meter _meter = new(MeterName);

    /// <summary>Counter of triggered callback executions by owner.</summary>
    public static readonly Counter<long> TriggersFired =
        _meter.CreateCounter<long>("watchpoint.scheduling.triggers.fired", "triggers", "Number of triggers fired by owner");

    /// <summary>Histogram of lateness in milliseconds between scheduled and actual firing.</summary>
    public static readonly Histogram<double> TriggerLateness =
        _meter.CreateHistogram<double>("watchpoint.scheduling.triggers.lateness", "ms", "Milliseconds between scheduled and fired time");
}

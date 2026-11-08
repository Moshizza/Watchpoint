using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Watchpoint.Hosting;

/// <summary>
/// Serilog enricher / destructuring policy that redacts sensitive properties (spec HST §6.3, HST-U-012).
/// Property names matching (?i)(password|secret|token|apikey|authorization|cookie) -> "***".
/// </summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    private static readonly Regex SensitiveRegex = new(
        @"(?i)(password|secret|token|apikey|authorization|cookie)",
        RegexOptions.Compiled);

    /// <summary>Redacted mask replacement.</summary>
    public const string Mask = "***";

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        var sensitiveKeys = new List<string>();
        foreach (var property in logEvent.Properties)
        {
            if (SensitiveRegex.IsMatch(property.Key))
            {
                sensitiveKeys.Add(property.Key);
            }
        }

        foreach (var key in sensitiveKeys)
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(key, new ScalarValue(Mask)));
        }
    }
}

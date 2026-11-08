using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace Watchpoint.Hosting;

/// <summary>
/// Serilog enricher that extracts module name from logger category (spec HST §6.3, HST-U-013).
/// Category: <c>Watchpoint.&lt;Module&gt;.*</c> -> property <c>Module=&lt;Module&gt;</c>.
/// </summary>
public sealed class ModuleEnricher : ILogEventEnricher
{
    private static readonly Regex ModuleRegex = new(@"^Watchpoint\.([A-Za-z0-9]+)", RegexOptions.Compiled);

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContextValue) &&
            sourceContextValue is ScalarValue { Value: string sourceContext })
        {
            var match = ModuleRegex.Match(sourceContext);
            if (match.Success)
            {
                var moduleName = match.Groups[1].Value;
                logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("Module", moduleName));
            }
        }
    }
}

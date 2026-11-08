using Microsoft.Extensions.Configuration;

namespace Watchpoint.Hosting;

/// <summary>
/// Execution context passed to modules during service configuration.
/// </summary>
public sealed class ModuleContext
{
    /// <summary>Gets the bound and validated node settings.</summary>
    public required NodeSettings Node { get; init; }

    /// <summary>Gets the full configuration root.</summary>
    public required IConfiguration Configuration { get; init; }

    /// <summary>Gets the resolved node paths.</summary>
    public required NodePaths Paths { get; init; }

    /// <summary>Gets a value indicating whether the node is running in development environment.</summary>
    public required bool IsDevelopment { get; init; }
}

using Watchpoint.Model.Documents;

namespace Watchpoint.Integrations.Contracts;

/// <summary>Settings of the integration runtime (spec IRT §4.1, §9); set by the host from the node settings.</summary>
public sealed class IntegrationsOptions
{
    /// <summary>Gets or sets the folder of the packages shipped with the product (<c>Integrations:BuiltinPath</c>); read-only.</summary>
    public required string BuiltinPath { get; set; }

    /// <summary>Gets or sets the folder of the packages installed by users (<c>Paths:Packages</c>).</summary>
    public required string PackagesPath { get; set; }

    /// <summary>Gets or sets the folder of the unpacked, verified copies (<c>&lt;Data&gt;/integrations/.cache</c>).</summary>
    public required string CachePath { get; set; }

    /// <summary>Gets or sets the folder of the development public keys (<c>&lt;Data&gt;/integrations/dev-keys</c>).</summary>
    public required string DevKeysPath { get; set; }

    /// <summary>Gets or sets whether the node runs with <c>Node:Environment = Development</c> (development keys are trusted).</summary>
    public bool IsDevelopment { get; set; }

    /// <summary>Gets or sets the product version compared with a manifest's <c>minWatchpointVersion</c>.</summary>
    public SemVersion ProductVersion { get; set; } = new(1, 0, 0);

    /// <summary>Gets or sets the period of the safety-net rescan (<c>Integrations:RescanInterval</c>).</summary>
    public TimeSpan RescanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Gets or sets how long the user folder must stay quiet before a change is processed.</summary>
    public TimeSpan Debounce { get; set; } = TimeSpan.FromSeconds(2);
}

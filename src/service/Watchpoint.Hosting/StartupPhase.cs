namespace Watchpoint.Hosting;

/// <summary>
/// Ordered phases of the Watchpoint node startup sequence.
/// </summary>
public enum StartupPhase
{
    /// <summary>Secrets, storage connections, NATS connection.</summary>
    Infrastructure = 0,

    /// <summary>Database migrations.</summary>
    Schema = 10,

    /// <summary>Type catalogue, permission catalogue.</summary>
    Catalogue = 20,

    /// <summary>Load configuration version into memory.</summary>
    Configuration = 30,

    /// <summary>State snapshot + log replay.</summary>
    Recovery = 40,

    /// <summary>Pipelines, engines, background services.</summary>
    Runtime = 50,

    /// <summary>HTTP endpoints accept traffic (readiness true).</summary>
    Edge = 60,

    /// <summary>Start integration connections.</summary>
    Connections = 70,
}

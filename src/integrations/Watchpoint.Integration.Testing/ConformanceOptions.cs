namespace Watchpoint.Integration.Testing;

/// <summary>
/// Conformance suite execution options indicating supported features, limits and timing expectations.
/// </summary>
public sealed class ConformanceOptions
{
    /// <summary>Whether the connector authenticates with credentials/secrets.</summary>
    public bool SupportsCredentials { get; set; }

    /// <summary>Whether the connector uses TLS connections.</summary>
    public bool SupportsTls { get; set; }

    /// <summary>Whether the connector supports event catch-up after reconnection.</summary>
    public bool SupportsEventCatchUp { get; set; } = true;

    /// <summary>Whether the connector supports executing commands.</summary>
    public bool SupportsCommands { get; set; } = true;

    /// <summary>Whether the connector supports batch command execution.</summary>
    public bool SupportsBatchCommands { get; set; } = true;

    /// <summary>Whether the connector uses paged APIs.</summary>
    public bool SupportsPaging { get; set; }

    /// <summary>Whether the connector supports per-device polling loops.</summary>
    public bool SupportsPerDevicePolling { get; set; }

    /// <summary>Expected polling or push reporting latency.</summary>
    public TimeSpan DeclaredLatency { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Expected command execution deadline timeout.</summary>
    public TimeSpan CommandDeadline { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Expected timeout for connectivity test.</summary>
    public TimeSpan TestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Expected timeout for StartAsync completion.</summary>
    public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Expected timeout for StopAsync completion.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

namespace Watchpoint.Testing.Containers;

/// <summary>
/// Container images used by the test fixtures, pinned by digest (spec TST §5.1). Updated only by the
/// dependency-upgrade task, never ad hoc; keep the PostgreSQL major version equal to the bundled one.
/// </summary>
public static class ContainerImages
{
    /// <summary>PostgreSQL 17 with TimescaleDB 2.31.0.</summary>
    public const string TimescaleDb =
        "timescale/timescaledb:2.31.0-pg17@sha256:b56a3f51991df1f79d0c1a0841cad5c5fa8058fdd6a3ee6886394a6d4c174538";

    /// <summary>NATS server 2.15.2 (started with JetStream enabled).</summary>
    public const string Nats =
        "nats:2.15.2-alpine@sha256:afae2fdbe2e326c65e1246cb83b9d05d319188734f3bd577ac8072daae78a10f";
}

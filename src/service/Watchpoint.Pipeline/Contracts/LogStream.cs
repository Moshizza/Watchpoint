using System.Diagnostics.CodeAnalysis;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>The streams of the event log (spec PIP §5.1).</summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Name fixed by spec PIP §5.4: it names a JetStream stream.")]
public enum LogStream
{
    /// <summary><c>WP_LOG</c>: device-bound records, 64 partitions.</summary>
    Log = 0,

    /// <summary><c>WP_CFG</c>: configuration and registry changes, globally ordered.</summary>
    Cfg = 1,

    /// <summary><c>WP_SYS</c>: system records not bound to a device, globally ordered.</summary>
    Sys = 2,
}

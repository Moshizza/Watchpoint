namespace Watchpoint.Hosting.Supervision;

/// <summary>How the supervisor decides that a started child is ready (spec HST §7.1).</summary>
public abstract record ReadinessProbe
{
    /// <summary>No probe: the child is ready as soon as it is started.</summary>
    public static readonly ReadinessProbe None = new NoProbe();

    private ReadinessProbe()
    {
    }

    /// <summary>Ready when a TCP connection to <paramref name="Host"/>:<paramref name="Port"/> succeeds within <paramref name="Timeout"/>.</summary>
    /// <param name="Host">The host name or address.</param>
    /// <param name="Port">The TCP port.</param>
    /// <param name="Timeout">The time allowed from the start of the child.</param>
    public sealed record Tcp(string Host, int Port, TimeSpan Timeout) : ReadinessProbe;

    /// <summary>Ready when <c>GET</c> <paramref name="Url"/> answers with a 2xx status within <paramref name="Timeout"/>.</summary>
    /// <param name="Url">The URL.</param>
    /// <param name="Timeout">The time allowed from the start of the child.</param>
    public sealed record Http(Uri Url, TimeSpan Timeout) : ReadinessProbe;

    /// <summary>Ready when a stdout line containing <paramref name="Contains"/> (ordinal) is seen within <paramref name="Timeout"/>.</summary>
    /// <param name="Contains">The text to look for.</param>
    /// <param name="Timeout">The time allowed from the start of the child.</param>
    public sealed record StdoutLine(string Contains, TimeSpan Timeout) : ReadinessProbe;

    private sealed record NoProbe : ReadinessProbe;
}

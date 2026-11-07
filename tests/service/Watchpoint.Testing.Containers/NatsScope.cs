namespace Watchpoint.Testing.Containers;

/// <summary>
/// Subject and stream prefixes isolating one test class on the shared NATS server, e.g. subject prefix
/// <c>t1a2b3c4d.wp</c> and stream prefix <c>T1A2B3C4D_</c>. Modules using NATS accept these prefixes via options (PIP §8).
/// </summary>
/// <param name="SubjectPrefix">The subject prefix (no trailing dot).</param>
/// <param name="StreamPrefix">The stream name prefix (ends with an underscore).</param>
public sealed record NatsScope(string SubjectPrefix, string StreamPrefix);

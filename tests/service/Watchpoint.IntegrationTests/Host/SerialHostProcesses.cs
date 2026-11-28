using Xunit;

namespace Watchpoint.IntegrationTests.Host;

/// <summary>
/// Tests that start <c>Watchpoint.Host</c> processes (each with a NATS container or a bundled nats-server) run one at a
/// time, after the parallel tests: several hosts starting together on a 4-core CI runner, next to every other
/// integration test, miss their start-up deadlines, and a host that loses a start-up race for a port exits at once.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialHostProcesses
{
    public const string Name = "Host processes";
}

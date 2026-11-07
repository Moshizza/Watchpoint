using Watchpoint.Testing.Containers;
using Xunit;
using Xunit.v3;

[assembly: Parallelization(MaxThreads = -1)]
[assembly: AssemblyFixture(typeof(PostgresContainerFixture))]
[assembly: AssemblyFixture(typeof(NatsContainerFixture))]

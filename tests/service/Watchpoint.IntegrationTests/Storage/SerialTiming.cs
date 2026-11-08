using Xunit;

namespace Watchpoint.IntegrationTests.Storage;

/// <summary>
/// Classes measuring timings (STO-I-011) run alone, after the parallel tests: on a shared CI runner, concurrent integration
/// tests use the same PostgreSQL server and CPU and would make the measurement meaningless.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTiming
{
    public const string Name = "Serial timing";
}

using Xunit;

namespace Watchpoint.CiProbe.Tests;

public sealed class CiProbeTests
{
    [Fact]
    public void Ci_004_FailingTestFailsCi() => Assert.Fail("Deliberate failure for CI-004; this commit is reverted.");
}

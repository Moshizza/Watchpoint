using Shouldly;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.Testing.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed class TimingLimitsTests
{
    private static TimeSpan Strict => TimeSpan.FromMilliseconds(500);
    private static TimeSpan Ceiling => TimeSpan.FromSeconds(2);

    [Theory]
    [InlineData(null, null, 500)]
    [InlineData("false", null, 500)]
    [InlineData("true", null, 2000)]
    [InlineData("TRUE", null, 2000)]
    [InlineData("true", "0", 2000)]
    [InlineData("true", "1", 500)]
    [InlineData(null, "1", 500)]
    public void TST_U_014_Timing_limit_is_strict_except_on_shared_CI(string? ci, string? strict, int expectedMs)
    {
        var environment = new Dictionary<string, string?> { ["CI"] = ci, [TimingLimits.StrictVariable] = strict };

        TimingLimits.For(Strict, Ceiling, name => environment.GetValueOrDefault(name)).ShouldBe(TimeSpan.FromMilliseconds(expectedMs));
    }

    [Fact]
    public void TST_U_014_Timing_limit_rejects_ceiling_below_strict_limit()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => TimingLimits.For(Ceiling, Strict, _ => null));
    }
}

using Shouldly;
using Watchpoint.Testing.Async;
using Xunit;

namespace Watchpoint.Testing.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed class EventuallyTests
{
    [Fact]
    public async Task TST_U_006_Eventually_returns_when_condition_becomes_true()
    {
        var polls = 0;

        await Eventually.UntilAsync(() => ++polls >= 3, pollInterval: TimeSpan.FromMilliseconds(1), ct: TestContext.Current.CancellationToken);

        polls.ShouldBe(3);
    }

    [Fact]
    public async Task TST_U_006_Eventually_returns_probed_value()
    {
        var polls = 0;

        var value = await Eventually.UntilAsync(() => ++polls >= 3 ? "ready" : null, pollInterval: TimeSpan.FromMilliseconds(1), ct: TestContext.Current.CancellationToken);

        value.ShouldBe("ready");
    }

    [Fact]
    public async Task TST_U_007_Eventually_fails_with_message()
    {
        var ex = await Should.ThrowAsync<TestAssertionException>(
            () => Eventually.UntilAsync(() => false, TimeSpan.FromMilliseconds(100), "the device never connected", ct: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("the device never connected");
        ex.Message.ShouldContain("100 ms");
    }
}

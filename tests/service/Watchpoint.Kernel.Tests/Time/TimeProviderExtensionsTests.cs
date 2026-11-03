using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Watchpoint.Kernel.Time;
using Xunit;

namespace Watchpoint.Kernel.Tests.Time;

public sealed class TimeProviderExtensionsTests
{
    [Fact]
    public void K_U_028_UtcNowMs_truncates_to_milliseconds()
    {
        var start = new DateTimeOffset(2026, 3, 29, 1, 59, 30, TimeSpan.Zero).AddTicks(1_234_567);
        var time = new FakeTimeProvider(start);

        var now = time.UtcNowMs();

        now.ShouldBe(new DateTimeOffset(2026, 3, 29, 1, 59, 30, 123, TimeSpan.Zero));
        now.Offset.ShouldBe(TimeSpan.Zero);
        (now.Ticks % TimeSpan.TicksPerMillisecond).ShouldBe(0);
    }
}

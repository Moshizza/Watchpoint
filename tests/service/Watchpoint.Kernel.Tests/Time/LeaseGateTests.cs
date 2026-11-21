using Shouldly;
using Watchpoint.Kernel.Time;
using Xunit;

namespace Watchpoint.Kernel.Tests.Time;

/// <summary>The single-node lease gate (spec K §6.1; T1.09c).</summary>
public sealed class LeaseGateTests
{
    [Fact]
    public async Task K_U_063_AlwaysHeldLeaseGate_completes_at_once_with_a_token_that_is_never_cancelled()
    {
        var gate = new AlwaysHeldLeaseGate();

        var wait = gate.WaitUntilHeldAsync("x", TestContext.Current.CancellationToken).AsTask();

        wait.IsCompletedSuccessfully.ShouldBeTrue();
        var held = await wait;
        held.CanBeCanceled.ShouldBeFalse();

        // An already cancelled wait is cancelled; an empty lease name is a programming error.
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await gate.WaitUntilHeldAsync("x", cancelled.Token));
        await Should.ThrowAsync<ArgumentException>(async () => await gate.WaitUntilHeldAsync(" ", CancellationToken.None));
    }
}

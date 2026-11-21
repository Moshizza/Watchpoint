using Shouldly;
using Watchpoint.Kernel.Values;
using Watchpoint.Pipeline.Contracts;
using Watchpoint.State.Tests.Support;
using Xunit;

namespace Watchpoint.State.Tests;

public sealed class ConcurrencyTests
{
    [Fact]
    public async Task STA_U_032_Concurrent_reads_during_writes_are_consistent()
    {
        var fixture = new StateFixture();
        fixture.Types.Add(TestTypes.Door());
        var device = TestEntities.Door();
        fixture.Registry.CreateDevice(device);
        var partition = Partitioning.Of(device.Id.Value);

        const int writes = 2000;
        var inconsistencies = 0;
        var ct = TestContext.Current.CancellationToken;
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 50_000; i++)
            {
                var state = fixture.Store.Get(device.Id.Value).Value;
                var property = state.Properties[TestTypes.Position];

                // A torn read would show the entity's overall sequence behind the property it was just set from.
                if (property.Sequence > state.Sequence)
                {
                    Interlocked.Increment(ref inconsistencies);
                }
            }
        }, ct)).ToArray();

        for (var i = 0; i < writes; i++)
        {
            var record = fixture.Records.Property(device.Id, null, TestTypes.Position, i % 2 == 0 ? WpValue.Enum("open") : WpValue.Enum("closed"));
            fixture.Apply(record);
        }

        await Task.WhenAll(readers);

        inconsistencies.ShouldBe(0);
    }
}

using Shouldly;
using Watchpoint.Testing.Ids;
using Xunit;

namespace Watchpoint.Testing.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed class TestIdsTests
{
    [Fact]
    public void TST_U_002_TestIds_are_deterministic_and_version_7_shaped()
    {
        var a = TestIds.Device(1);
        var b = TestIds.Device(1);

        a.ShouldBe(b);
        a.Value.Version.ShouldBe(7);
        a.Value.Variant.ShouldBe(8);
        a.ToString().ShouldBe("00000000-0000-7000-8000-000000000001");
        TestIds.Device(42).ToString().ShouldBe("00000000-0000-7000-8000-000000000042");
        TestIds.Server(1).Value.ShouldBe(a.Value);
        Should.Throw<ArgumentOutOfRangeException>(() => TestIds.Uuid(-1));
    }
}

using Shouldly;
using Watchpoint.Testing.Json;
using Xunit;

namespace Watchpoint.Testing.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed class JsonAssertTests
{
    [Fact]
    public void TST_U_008_JsonAssert_ignores_order_and_listed_paths()
    {
        const string expected = """{ "a": 1, "b": { "c": [1, 2], "d": "x" }, "traceId": "00-aaa" }""";
        const string actual = """{"traceId":"00-bbb","b":{"d":"x","c":[1,2.0]},"a":1.0}""";

        Should.NotThrow(() => JsonAssert.Equivalent(expected, actual, JsonAssertOptions.Ignoring("/traceId")));
    }

    [Fact]
    public void TST_U_008_JsonAssert_reports_first_difference()
    {
        var ex = Should.Throw<TestAssertionException>(() => JsonAssert.Equivalent("""{"b":{"c":[1,2]}}""", """{"b":{"c":[1,3]}}"""));

        ex.Message.ShouldContain("/b/c/1");
        Should.Throw<TestAssertionException>(() => JsonAssert.Equivalent("""{"traceId":"a"}""", """{"traceId":"b"}"""));
        Should.Throw<TestAssertionException>(() => JsonAssert.Equivalent("""{"a":1}""", """{"a":1,"b":2}"""));
    }
}

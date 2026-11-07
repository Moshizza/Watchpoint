using Shouldly;
using Xunit;

namespace Watchpoint.Analyzers.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed class AnalyzerTests
{
    [Fact]
    public Task TST_U_009_WP0001_reports_default_Result() => AnalyzerHarness.VerifyAsync(
        new DefaultResultAnalyzer(),
        """
        using Watchpoint.Kernel.Results;
        public static class C
        {
            public static void M()
            {
                var a = [|default(Result<int>)|];
                var b = [|default(Result)|];
                Result<string> c = [|default|];
                var d = [|new Result<int>()|];
                var ok1 = Result.Success();
                var ok2 = new Result<int>(5);
                var ok3 = default(int);
            }
        }
        """);

    [Fact]
    public Task TST_U_010_WP0002_reports_a_badly_named_test() => AnalyzerHarness.VerifyAsync(
        new TestNamingAnalyzer(),
        """
        using Xunit;
        public class Tests
        {
            [Fact] public void [|Works|]() { }
            [Theory] public void [|REG_012_Missing_layer|]() { }
            [Fact] public void [|reg_U_001_lowercase|]() { }
            [Fact] public void REG_U_001_Works() { }
            [Theory] public void PIP_I_003_Replays() { }
            [Fact] public void X_API_REG_C_001_Contract() { }
            public void Helper() { }
        }
        """,
        testCode: true);

    [Fact]
    public Task TST_U_011_WP0003_reports_DateTime_UtcNow() => AnalyzerHarness.VerifyAsync(
        new WallClockAnalyzer(),
        """
        using System;
        using System.Diagnostics;
        public static class C
        {
            public static void M(TimeProvider time)
            {
                var a = [|DateTime.UtcNow|];
                var b = [|DateTime.Now|];
                var c = [|DateTimeOffset.UtcNow|];
                var d = [|DateTimeOffset.Now|];
                var e = [|Stopwatch.StartNew()|];
                var ok = time.GetUtcNow();
                var ok2 = DateTime.MinValue;
            }
        }
        """);

    [Fact]
    public async Task TST_U_011_WP0003_is_not_reported_in_test_code()
    {
        var diagnostics = await AnalyzerHarness.GetDiagnosticsAsync(
            new WallClockAnalyzer(),
            "public static class C { public static System.DateTime M() => System.DateTime.UtcNow; }",
            testCode: true);

        diagnostics.ShouldBeEmpty();
    }

    [Fact]
    public async Task TST_U_011_WP0003_code_fix_uses_the_TimeProvider_in_scope()
    {
        var fixedSource = await AnalyzerHarness.ApplyFixAsync(
            new WallClockAnalyzer(),
            new WallClockCodeFixProvider(),
            """
            using System;
            public sealed class Clocked(TimeProvider time)
            {
                public DateTimeOffset A() => DateTimeOffset.UtcNow;
                public DateTime B() => DateTime.UtcNow;
                public DateTimeOffset C() => DateTimeOffset.Now;
                public static DateTime D() => DateTime.Now;
            }
            """);

        fixedSource.ShouldBe(
            """
            using System;
            public sealed class Clocked(TimeProvider time)
            {
                public DateTimeOffset A() => time.GetUtcNow();
                public DateTime B() => time.GetUtcNow().UtcDateTime;
                public DateTimeOffset C() => time.GetLocalNow();
                public static DateTime D() => TimeProvider.System.GetLocalNow().DateTime;
            }
            """);
    }

    [Fact]
    public Task TST_U_012_WP0004_reports_Result() => AnalyzerHarness.VerifyAsync(
        new BlockingWaitAnalyzer(),
        """
        using System.Threading.Tasks;
        public static class C
        {
            public static async Task M(Task<int> t, Task u, ValueTask<int> v)
            {
                var a = [|t.Result|];
                [|u.Wait()|];
                [|u.GetAwaiter().GetResult()|];
                var b = [|t.ConfigureAwait(false).GetAwaiter().GetResult()|];
                var c = [|v.Result|];
                var ok = await t;
                Task.WaitAll();
            }

            public static void Main() => Task.CompletedTask.GetAwaiter().GetResult();
        }
        """);

    [Fact]
    public Task TST_U_013_WP0005_reports_Thread_Sleep_in_a_test_project() => AnalyzerHarness.VerifyAsync(
        new SleepInTestsAnalyzer(),
        """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        public static class C
        {
            public static async Task M(TimeProvider time, CancellationToken ct)
            {
                [|Thread.Sleep(10)|];
                await [|Task.Delay(10)|];
                await [|Task.Delay(TimeSpan.FromSeconds(1), ct)|];
                await Task.Delay(TimeSpan.FromSeconds(1), time, ct);
            }
        }
        """,
        testCode: true);

    [Fact]
    public async Task TST_U_013_WP0005_is_not_reported_outside_test_projects()
    {
        var diagnostics = await AnalyzerHarness.GetDiagnosticsAsync(
            new SleepInTestsAnalyzer(),
            "public static class C { public static void M() => System.Threading.Thread.Sleep(1); }",
            testCode: false);

        diagnostics.ShouldBeEmpty();
    }
}

using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Kernel.Tests.Results;

public sealed class ResultTests
{
    private static readonly Error _notFound = CommonErrors.NotFound("device", Guid.CreateVersion7());

    [Fact]
    public void K_U_016_Success_result_exposes_value()
    {
        Result<int> r = 5;

        r.IsSuccess.ShouldBeTrue();
        r.Value.ShouldBe(5);
        Should.Throw<InvalidOperationException>(() => r.Error);
        r.TryGetValue(out var value, out var error).ShouldBeTrue();
        value.ShouldBe(5);
        error.ShouldBeNull();

        Result.Success().IsSuccess.ShouldBeTrue();
        Should.Throw<InvalidOperationException>(() => Result.Success().Error);
    }

    [Fact]
    public void K_U_017_Failure_result_exposes_error()
    {
        Result<int> r = _notFound;

        r.IsSuccess.ShouldBeFalse();
        r.Error.ShouldBeSameAs(_notFound);
        Should.Throw<InvalidOperationException>(() => r.Value);
        r.TryGetValue(out _, out var error).ShouldBeFalse();
        error.ShouldBeSameAs(_notFound);

        Result plain = _notFound;
        plain.IsSuccess.ShouldBeFalse();
        plain.Error.ShouldBeSameAs(_notFound);
    }

    [Fact]
    public void K_U_018_Default_result_access_throws()
    {
        var r = default(Result<int>);
        r.IsSuccess.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => r.Error).Message.ShouldBe("uninitialised result");
        Should.Throw<InvalidOperationException>(() => r.Value).Message.ShouldBe("uninitialised result");

        var plain = default(Result);
        plain.IsSuccess.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => plain.Error).Message.ShouldBe("uninitialised result");
    }

    [Fact]
    public void K_U_019_Map_on_success_and_failure()
    {
        Result<int> ok = 5;
        ok.Map(x => x * 2).Value.ShouldBe(10);

        Result<int> failed = _notFound;
        var called = false;
        var mapped = failed.Map(x =>
        {
            called = true;
            return x.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        called.ShouldBeFalse();
        mapped.Error.ShouldBeSameAs(_notFound);
    }

    [Fact]
    public void K_U_020_Bind_short_circuits()
    {
        var secondCalled = false;
        Result<int> First(int x) => _notFound;
        Result<int> Second(int x)
        {
            secondCalled = true;
            return x;
        }

        var result = Result.Success(1).Bind(First).Bind(Second);

        secondCalled.ShouldBeFalse();
        result.Error.ShouldBeSameAs(_notFound);
        Result.Success(1).Bind(x => Result.Success(x + 1)).Value.ShouldBe(2);
    }

    [Fact]
    public void K_U_021_Ensure()
    {
        var tooSmall = CommonErrors.OutOfRange(10, 20);

        Result.Success(15).Ensure(x => x >= 10, tooSmall).Value.ShouldBe(15);
        Result.Success(5).Ensure(x => x >= 10, tooSmall).Error.ShouldBeSameAs(tooSmall);
        Result.Failure<int>(_notFound).Ensure(x => x >= 10, tooSmall).Error.ShouldBeSameAs(_notFound);
    }

    [Fact]
    public void K_U_022_Combine_merges_validation_errors()
    {
        var a = CommonErrors.ValidationFailed([CommonErrors.RequiredAt("/name")]);
        var b = CommonErrors.ValidationFailed([CommonErrors.TooLongAt("/label", 10), CommonErrors.OutOfRangeAt("/port", 1, 65535)]);

        var combined = ResultExtensions.Combine(Result.Success(), a, Result.Success(), b);

        combined.IsSuccess.ShouldBeFalse();
        combined.Error.Kind.ShouldBe(ErrorKind.Validation);
        combined.Error.Code.ShouldBe(CommonErrorCodes.ValidationFailed);
        combined.Error.FieldErrors.Select(f => f.Path).ShouldBe(["/name", "/label", "/port"]);

        ResultExtensions.Combine(Result.Success(), Result.Success()).IsSuccess.ShouldBeTrue();
        ResultExtensions.Combine(Result.Success(), a).Error.ShouldBeSameAs(a);
    }

    [Fact]
    public void K_U_023_Combine_with_a_non_validation_error()
    {
        var validation = CommonErrors.ValidationFailed([CommonErrors.RequiredAt("/name")]);
        var conflict = Error.Conflict("registry.device.duplicate");

        ResultExtensions.Combine(validation, _notFound, conflict).Error.ShouldBeSameAs(_notFound);
        ResultExtensions.Combine(conflict, validation, _notFound).Error.ShouldBeSameAs(conflict);
    }

    [Fact]
    public void K_U_024_MessageKey_derived_from_code()
    {
        Error.NotFound("registry.device.not_found").MessageKey.ShouldBe("errors.registry.device.not_found");
        CommonErrors.RequiredAt("/name").MessageKey.ShouldBe("errors.common.value.required");
    }

    [Fact]
    public void K_U_025_Disallowed_argument_types_rejected()
    {
        Should.Throw<ArgumentException>(() => Error.NotFound("x.y", ("a", new object())));
        Should.Throw<ArgumentException>(() => Error.NotFound("x.y", ("a", 1.5f)));
        Should.Throw<ArgumentException>(() => Error.NotFound("x.y", ("a", new List<int>())));
        Should.Throw<ArgumentException>(() => Error.NotFound("x.y", ("NotCamel", 1)));
        Should.Throw<ArgumentException>(() => Error.NotFound("x.y").WithArg("a", DateTime.UtcNow));
        Should.Throw<ArgumentException>(() => FieldError.Create("/a", ErrorCode.FromTrusted("x.y"), ("a", new object())));
        Should.Throw<ArgumentException>(() => Error.NotFound("Not a code"));

        var allowed = Error.NotFound(
            "x.y",
            ("text", "s"),
            ("int", 1),
            ("long", 1L),
            ("double", 1.5),
            ("decimal", 1.5m),
            ("bool", true),
            ("time", DateTimeOffset.UnixEpoch),
            ("guid", Guid.NewGuid()),
            ("device", DeviceId.New()),
            ("type", TypeId.FromTrusted("ac.door")),
            ("list", new List<string> { "a", "b" }),
            ("none", null));
        allowed.Args.Count.ShouldBe(12);
    }

    [Fact]
    public async Task K_U_026_Async_extensions_Async()
    {
        static Task<Result<int>> Ok(int x) => Task.FromResult(Result.Success(x));
        static Task<Result<int>> Failed() => Task.FromResult(Result.Failure<int>(_notFound));

        (await Ok(2).Map(x => x * 3)).Value.ShouldBe(6);
        (await Failed().Map(x => x * 3)).Error.ShouldBeSameAs(_notFound);

        (await Ok(2).Bind(x => Result.Success(x + 1))).Value.ShouldBe(3);
        (await Ok(2).BindAsync(x => Ok(x + 10))).Value.ShouldBe(12);
        (await Failed().BindAsync(x => Ok(x + 10))).Error.ShouldBeSameAs(_notFound);
        (await Ok(2).Ensure(x => x > 5, _notFound)).Error.ShouldBeSameAs(_notFound);
        (await Failed().Match(x => "ok", e => e.Code.Value)).ShouldBe("common.not_found");

        var tapped = 0;
        (await Ok(4).Tap(x => tapped = x)).Value.ShouldBe(4);
        tapped.ShouldBe(4);

        var replacement = Error.Unavailable("x.unavailable");
        (await Failed().MapError(_ => replacement)).Error.ShouldBeSameAs(replacement);

        static ValueTask<Result<int>> OkValue(int x) => ValueTask.FromResult(Result.Success(x));
        static ValueTask<Result<int>> FailedValue() => ValueTask.FromResult(Result.Failure<int>(_notFound));
        (await OkValue(2).Map(x => x * 3)).Value.ShouldBe(6);
        (await FailedValue().Map(x => x * 3)).Error.ShouldBeSameAs(_notFound);
        (await OkValue(2).BindAsync(x => OkValue(x + 1))).Value.ShouldBe(3);
        (await FailedValue().Ensure(x => x > 0, replacement)).Error.ShouldBeSameAs(_notFound);
    }

    [Fact]
    public void K_U_027_WithArg_returns_a_new_error()
    {
        var original = Error.NotFound("registry.device.not_found", ("id", "a"));

        var changed = original.WithArg("site", "s1").WithArg("id", "b");

        changed.ShouldNotBeSameAs(original);
        changed.Args["id"].ShouldBe("b");
        changed.Args["site"].ShouldBe("s1");
        original.Args.Count.ShouldBe(1);
        original.Args["id"].ShouldBe("a");
    }
}

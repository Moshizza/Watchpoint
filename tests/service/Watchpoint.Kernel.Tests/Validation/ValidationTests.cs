using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Validation;
using Xunit;

namespace Watchpoint.Kernel.Tests.Validation;

public sealed partial class ValidationTests
{
    [Fact]
    public void K_U_051_ValidationBuilder_collects_all_errors()
    {
        var logger = new ListLogger();
        var result = new ValidationBuilder(logger)
            .Required("/name", null)
            .Range("/settings/port", 70000, 1, 65535)
            .Pattern("/code", "ABC", CodePattern(), "lowercase letters")
            .MaxLength("/description", "short", 2000)
            .Custom("/items", () => [CommonErrors.DuplicateAt("/1/name", "a")])
            .Build();

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(CommonErrorCodes.ValidationFailed);
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.FieldErrors.Select(e => (e.Path, e.Code.Value)).ShouldBe(
        [
            ("/name", "common.value.required"),
            ("/settings/port", "common.value.out_of_range"),
            ("/code", "common.value.invalid_format"),
            ("/items/1/name", "common.value.duplicate"),
        ]);
        result.Error.FieldErrors[1].Args["max"].ShouldBe(65535L);
        result.Error.FieldErrors[2].Args["expected"].ShouldBe("lowercase letters");

        logger.Entries.ShouldBe([(1002, LogLevel.Debug, "Validation failed with 4 field errors at /name,/settings/port,/code,/items/1/name")]);

        new ValidationBuilder().Name("/name", " x").MaxLength("/d", new string('x', 3), 2).Build().Error.FieldErrors
            .Select(e => e.Code.Value).ShouldBe(["common.value.invalid_format", "common.value.too_long"]);
    }

    [Fact]
    public void K_U_052_ValidationBuilder_success()
    {
        var builder = new ValidationBuilder()
            .Required("/name", "Lobby")
            .Name("/name", "Lobby")
            .Name("/optional", null)
            .Range("/settings/port", 443, 1, 65535)
            .Range("/optional", null, 1, 2)
            .MaxLength("/description", null, 10)
            .Pattern("/code", "abc", CodePattern(), "lowercase letters")
            .Custom("/items", () => []);

        builder.IsValid.ShouldBeTrue();
        builder.Build().ShouldBe(Result.Success());
    }

    [Fact]
    public void K_U_053_JSON_Pointer_escaping()
    {
        JsonPointer.Escape("a/b~c").ShouldBe("a~1b~0c");
        JsonPointer.Combine("a/b~c").ShouldBe("/a~1b~0c");
        JsonPointer.Combine("settings", "a/b~c", "0").ShouldBe("/settings/a~1b~0c/0");
        JsonPointer.Combine().ShouldBe("");
        JsonPointer.Append("/items", 3).ShouldBe("/items/3");
        JsonPointer.Append("/map", "~/").ShouldBe("/map/~0~1");
    }

    [Fact]
    public void K_U_054_Guard_throws_argument_exceptions()
    {
        string? missing = null;
        Should.Throw<ArgumentNullException>(() => Guard.NotNull(missing)).ParamName.ShouldBe("missing");
        Guard.NotNull("x").ShouldBe("x");

        Should.Throw<ArgumentNullException>(() => Guard.NotEmpty(missing)).ParamName.ShouldBe("missing");
        var blank = " ";
        Should.Throw<ArgumentException>(() => Guard.NotEmpty(blank)).ParamName.ShouldBe("blank");
        Guard.NotEmpty("x").ShouldBe("x");

        int[] empty = [];
        Should.Throw<ArgumentException>(() => Guard.NotEmpty(empty)).ParamName.ShouldBe("empty");

        var port = 0;
        Should.Throw<ArgumentOutOfRangeException>(() => Guard.InRange(port, 1, 65535)).ParamName.ShouldBe("port");
        Guard.InRange(5, 1, 10).ShouldBe(5);
    }

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex CodePattern();

    private sealed class ListLogger : ILogger
    {
        public List<(int Id, LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((eventId.Id, logLevel, formatter(state, exception)));
    }
}

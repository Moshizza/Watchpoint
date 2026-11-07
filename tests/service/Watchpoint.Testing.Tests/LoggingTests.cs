using Microsoft.Extensions.Logging;
using Shouldly;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Testing.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed partial class LoggingTests
{
    [Fact]
    public void TST_U_003_LogCollector_captures_structured_properties()
    {
        using var collector = new LogCollector();
        using var factory = LoggerFactory.Create(b => b.AddProvider(collector).SetMinimumLevel(LogLevel.Trace));
        var logger = factory.CreateLogger("Test.Category");

        LogOpened(logger, "dev-1");

        var entry = collector.Entries.ShouldHaveSingleItem();
        entry.Category.ShouldBe("Test.Category");
        entry.Level.ShouldBe(LogLevel.Information);
        entry.EventId.ShouldBe(1001);
        entry.MessageTemplate.ShouldBe("Opened {DeviceId}");
        entry.RenderedMessage.ShouldBe("Opened dev-1");
        entry.Properties["DeviceId"].ShouldBe("dev-1");
        collector.WithEventId(1001).ShouldHaveSingleItem();
        collector.WithEventId(1002).ShouldBeEmpty();

        collector.Clear();
        collector.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("base64")]
    [InlineData("url")]
    [InlineData("exception")]
    public void TST_U_004_SecretLeakDetector_finds_plain_base64_and_url_encoded_leaks(string form)
    {
        using var collector = new LogCollector();
        using var factory = LoggerFactory.Create(b => b.AddProvider(collector));
        var logger = factory.CreateLogger("Leaky");
        var detector = new SecretLeakDetector();
        detector.Register("p@ss");

        switch (form)
        {
            case "plain":
                LogValue(logger, "user P@SS");
                break;
            case "base64":
                LogValue(logger, "cEBzcw==");
                break;
            case "url":
                LogValue(logger, "pwd=p%40ss");
                break;
            default:
                LogFailure(logger, new InvalidOperationException("login failed with p@ss"));
                break;
        }

        var ex = Should.Throw<TestAssertionException>(() => detector.AssertNoLeak(collector));
        ex.Message.ShouldContain("entry #0 [Leaky, event 1003]");
        ex.Message.ShouldNotContain("p@ss", Case.Insensitive);
    }

    [Fact]
    public void TST_U_005_SecretLeakDetector_passes_when_absent()
    {
        using var collector = new LogCollector();
        using var factory = LoggerFactory.Create(b => b.AddProvider(collector));
        var logger = factory.CreateLogger("Clean");
        var detector = new SecretLeakDetector();
        detector.Register("p@ss");

        LogValue(logger, "user ***");
        LogOpened(logger, "dev-1");

        Should.NotThrow(() => detector.AssertNoLeak(collector));
    }

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Opened {DeviceId}")]
    private static partial void LogOpened(ILogger logger, string deviceId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Value {Value}")]
    private static partial void LogValue(ILogger logger, string value);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Error, Message = "Failure")]
    private static partial void LogFailure(ILogger logger, Exception exception);
}

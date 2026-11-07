using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Xunit;

namespace Watchpoint.Kernel.Tests.Json;

public sealed class WatchpointJsonTests
{
    private static readonly JsonSerializerOptions _api = WatchpointJson.Create(JsonProfile.Api, TestJsonContext.Default);
    private static readonly JsonSerializerOptions _document = WatchpointJson.Create(JsonProfile.Document, TestJsonContext.Default);
    private static readonly JsonSerializerOptions _log = WatchpointJson.Create(JsonProfile.Log, TestJsonContext.Default);

    [Fact]
    public void K_U_037_Date_JSON_writing()
    {
        var json = JsonSerializer.Serialize(new DateHolder(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(1))), _api);

        json.ShouldBe("""{"at":"2026-11-03T09:00:00.000Z"}""");

        JsonSerializer.Serialize(new DateHolder(new DateTimeOffset(2026, 11, 3, 9, 0, 0, 123, TimeSpan.Zero).AddTicks(4567)), _api)
            .ShouldBe("""{"at":"2026-11-03T09:00:00.123Z"}""");
    }

    [Fact]
    public void K_U_038_Date_JSON_reading()
    {
        var read = JsonSerializer.Deserialize<DateHolder>("""{"at":"2026-11-03T10:00:00+01:00"}""", _api)!;
        read.At.ShouldBe(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero));
        read.At.Offset.ShouldBe(TimeSpan.Zero);

        JsonSerializer.Deserialize<DateHolder>("""{"at":"2026-11-03T09:00:00.5Z"}""", _api)!.At.Millisecond.ShouldBe(500);

        foreach (var bad in new[] { "\"2026-11-03T10:00:00\"", "\"2026-11-03\"", "\"03/11/2026 10:00 +01:00\"", "1762160400" })
        {
            var ex = Should.Throw<JsonException>(() => JsonSerializer.Deserialize<DateHolder>("{\"at\":" + bad + "}", _api));
            ex.Path.ShouldBe("$.at");
        }
    }

    [Fact]
    public void K_U_039_Duration_JSON()
    {
        Read("PT10S").ShouldBe(TimeSpan.FromSeconds(10));
        Read("P1D").ShouldBe(TimeSpan.FromDays(1));
        Read("PT1H30M").ShouldBe(TimeSpan.FromMinutes(90));
        Read("PT0.25S").ShouldBe(TimeSpan.FromMilliseconds(250));
        Read("-PT5M").ShouldBe(TimeSpan.FromMinutes(-5));

        Write(TimeSpan.FromSeconds(10)).ShouldBe("PT10S");
        Write(TimeSpan.FromDays(1)).ShouldBe("P1D");
        Write(TimeSpan.FromMinutes(90)).ShouldBe("PT1H30M");
        Write(TimeSpan.Zero).ShouldBe("PT0S");
        Write(new TimeSpan(1, 2, 0, 0, 500)).ShouldBe("P1DT2H0.5S");
        Write(TimeSpan.FromMinutes(-5)).ShouldBe("-PT5M");

        foreach (var bad in new[] { "P1M", "P1Y", "P1Y2D", "00:00:10", "P", "PT", "10", "" })
        {
            Should.Throw<JsonException>(() => Read(bad), bad);
        }

        static TimeSpan Read(string text) =>
            JsonSerializer.Deserialize<DurationHolder>("{\"duration\":\"" + text + "\"}", _api)!.Duration;

        static string Write(TimeSpan value)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new DurationHolder(value), _api));
            var text = doc.RootElement.GetProperty("duration").GetString()!;
            Read(text).ShouldBe(value);
            return text;
        }
    }

    [Fact]
    public void K_U_040_JsonProfile_Document_rejects_unknown_members()
    {
        var ex = Should.Throw<JsonException>(
            () => JsonSerializer.Deserialize<Settings>("""{"name":"a","port":1,"extra":true}""", _document));

        ex.Path.ShouldNotBeNull();
        ex.Message.ShouldContain("extra");
    }

    [Fact]
    public void K_U_041_JsonProfile_Api_ignores_unknown_members() =>
        JsonSerializer.Deserialize<Settings>("""{"name":"a","port":1,"extra":{"x":[1]}}""", _api).ShouldBe(new Settings("a", 1));

    [Fact]
    public void K_U_042_JsonProfile_Log_ignores_unknown_members() =>
        JsonSerializer.Deserialize<Settings>("""{"extra":1,"name":"a","port":1}""", _log).ShouldBe(new Settings("a", 1));

    [Fact]
    public void K_U_043_Enums_as_camelCase_strings()
    {
        JsonSerializer.Serialize(new KindHolder(ErrorKind.PreconditionFailed), _api).ShouldBe("""{"kind":"preconditionFailed"}""");
        JsonSerializer.Deserialize<KindHolder>("""{"kind":"notFound"}""", _api)!.Kind.ShouldBe(ErrorKind.NotFound);

        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<KindHolder>("""{"kind":3}""", _api));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<KindHolder>("""{"kind":"3"}""", _api));
    }

    [Fact]
    public void K_U_044_NaN_rejected()
    {
        Should.Throw<ArgumentException>(() => JsonSerializer.Serialize(new NumberHolder(double.NaN), _api));
        Should.Throw<ArgumentException>(() => JsonSerializer.Serialize(new NumberHolder(double.PositiveInfinity), _api));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<NumberHolder>("""{"number":"NaN"}""", _api));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<NumberHolder>("""{"number":"1.5"}""", _api));

        // The Kernel's own types are known without the caller's context.
        var kernelOnly = WatchpointJson.Create(JsonProfile.Document);
        JsonSerializer.Deserialize<LocalizedName>("""{"default":"Lobby"}""", kernelOnly)!.Translations.ShouldBeEmpty();
        kernelOnly.IsReadOnly.ShouldBeTrue();
    }
}

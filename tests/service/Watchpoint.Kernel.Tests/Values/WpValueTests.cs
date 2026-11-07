using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Json;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Tests.Json;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Kernel.Tests.Values;

public sealed class WpValueTests
{
    private static readonly JsonSerializerOptions _options = WatchpointJson.Create(JsonProfile.Log, TestJsonContext.Default);
    private static readonly DateTimeOffset _at = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void K_U_056_WpValue_factories_and_accessors()
    {
        using var doc = JsonDocument.Parse("""{"a":1}""");
        var all = AllKinds(doc.RootElement);

        default(WpValue).Kind.ShouldBe(WpValueKind.Null);
        WpValue.Null.IsNull.ShouldBeTrue();
        WpValue.Bool(true).AsBool().ShouldBeTrue();
        WpValue.Int(-5).AsInt().ShouldBe(-5);
        WpValue.Decimal(21.5m).AsDecimal().ShouldBe(21.5m);
        WpValue.Enum("open").AsEnum().ShouldBe("open");
        WpValue.String("text").AsString().ShouldBe("text");
        WpValue.Timestamp(_at).AsTimestamp().ShouldBe(_at);
        WpValue.Duration(TimeSpan.FromSeconds(10)).AsDuration().ShouldBe(TimeSpan.FromSeconds(10));
        WpValue.Geo(48.85, 2.35).AsGeo().ShouldBe(new GeoPoint(48.85, 2.35, null));
        WpValue.Geo(48.85, 2.35, 35).AsGeo().Altitude.ShouldBe(35);
        WpValue.List([WpValue.Int(1), WpValue.Int(2)]).AsList().ShouldBe([WpValue.Int(1), WpValue.Int(2)]);
        WpValue.Object(doc.RootElement).AsObject().GetProperty("a").GetInt32().ShouldBe(1);

        // Every accessor except the matching one throws; every TryAs except the matching one returns false.
        foreach (var value in all.Where(v => !v.IsNull))
        {
            var accessors = Accessors(value);
            accessors.Count(a => a.Kind == value.Kind).ShouldBe(1);
            foreach (var (kind, get, tryGet) in accessors)
            {
                if (kind == value.Kind)
                {
                    Should.NotThrow(get);
                    tryGet().ShouldBeTrue();
                }
                else
                {
                    Should.Throw<InvalidOperationException>(get);
                    tryGet().ShouldBeFalse();
                }
            }
        }

        Should.Throw<ArgumentNullException>(() => WpValue.String(null!));
        Should.Throw<ArgumentOutOfRangeException>(() => WpValue.Geo(91, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => WpValue.Geo(0, double.NaN));
        Should.Throw<ArgumentException>(() => WpValue.Object(JsonDocument.Parse("[1]").RootElement));

        // Lists are copied: changing the source does not change the value.
        var source = new List<WpValue> { WpValue.Int(1) };
        var list = WpValue.List(source);
        source.Add(WpValue.Int(2));
        list.AsList().Count.ShouldBe(1);
    }

    [Fact]
    public void K_U_057_WpValue_equality()
    {
        (WpValue.Decimal(1.0m) == WpValue.Decimal(1.00m)).ShouldBeTrue();
        WpValue.Decimal(1.0m).GetHashCode().ShouldBe(WpValue.Decimal(1.00m).GetHashCode());
        (WpValue.Int(1) != WpValue.Decimal(1)).ShouldBeTrue();
        WpValue.Enum("open").ShouldNotBe(WpValue.String("open"));
        WpValue.Null.ShouldBe(default);

        WpValue.List([WpValue.Int(1), WpValue.String("a")]).ShouldBe(WpValue.List([WpValue.Int(1), WpValue.String("a")]));
        WpValue.List([WpValue.Int(1), WpValue.String("a")]).GetHashCode()
            .ShouldBe(WpValue.List([WpValue.Int(1), WpValue.String("a")]).GetHashCode());
        WpValue.List([WpValue.Int(1)]).ShouldNotBe(WpValue.List([WpValue.Int(1), WpValue.Int(1)]));
        WpValue.List([WpValue.Decimal(2.50m)]).ShouldBe(WpValue.List([WpValue.Decimal(2.5m)]));

        var a = Object("""{"b":[1,2],"a":{"y":true,"x":"s"}}""");
        var b = Object("""{ "a": { "x": "s", "y": true }, "b": [1, 2] }""");
        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(Object("""{"b":[2,1],"a":{"y":true,"x":"s"}}"""));

        WpValue.Timestamp(_at).ShouldBe(WpValue.Timestamp(_at.ToOffset(TimeSpan.FromHours(2))));
        WpValue.Geo(1, 2).ShouldNotBe(WpValue.Geo(1, 2, 0));
    }

    [Fact]
    public void K_U_058_WpValue_JSON_round_trip_for_every_kind()
    {
        using var doc = JsonDocument.Parse("""{"door":{"open":true},"n":[1,2]}""");
        foreach (var value in AllKinds(doc.RootElement))
        {
            var json = JsonSerializer.Serialize(new ValueHolder(value), _options);
            JsonSerializer.Deserialize<ValueHolder>(json, _options)!.Value.ShouldBe(value, json);
        }

        Write(WpValue.Null).ShouldBe("""{"k":"null"}""");
        Write(WpValue.Bool(true)).ShouldBe("""{"k":"bool","v":true}""");
        Write(WpValue.Int(5)).ShouldBe("""{"k":"int","v":5}""");
        Write(WpValue.Decimal(21.50m)).ShouldBe("""{"k":"dec","v":"21.50"}""");
        Write(WpValue.Enum("open")).ShouldBe("""{"k":"enum","v":"open"}""");
        Write(WpValue.String("text")).ShouldBe("""{"k":"str","v":"text"}""");
        Write(WpValue.Timestamp(_at)).ShouldBe("""{"k":"ts","v":"2026-11-03T09:00:00.000Z"}""");
        Write(WpValue.Duration(TimeSpan.FromSeconds(10))).ShouldBe("""{"k":"dur","v":"PT10S"}""");
        Write(WpValue.Geo(48.85, 2.35)).ShouldBe("""{"k":"geo","v":[48.85,2.35]}""");
        Write(WpValue.List([WpValue.Int(1)])).ShouldBe("""{"k":"list","v":[{"k":"int","v":1}]}""");
        Write(Object("""{"a":1}""")).ShouldBe("""{"k":"obj","v":{"a":1}}""");

        Read("""{"v":"21.5","k":"dec"}""").ShouldBe(WpValue.Decimal(21.5m));
        Read("""{"k":"null","v":null}""").ShouldBe(WpValue.Null);

        foreach (var bad in new[]
        {
            """{"k":"dec","v":21.5}""", """{"k":"int","v":1.5}""", """{"k":"int","v":"1"}""", """{"k":"bool"}""",
            """{"k":"geo","v":[91,0]}""", """{"k":"geo","v":[1]}""", """{"k":"ts","v":"2026-11-03T09:00:00"}""",
            """{"k":"dur","v":"P1M"}""", """{"k":"unknown","v":1}""", """{"k":"int","v":1,"x":2}""", """{"k":"null","v":1}""",
            """{"k":"obj","v":[1]}""", """{"k":"list","v":[1]}""", "5", "null",
        })
        {
            Should.Throw<JsonException>(() => Read(bad), bad);
        }
    }

    [Fact]
    public void K_U_059_WpValue_timestamp_normalisation()
    {
        var value = WpValue.Timestamp(new DateTimeOffset(2026, 11, 3, 11, 0, 0, 123, TimeSpan.FromHours(2)).AddTicks(4567));

        value.AsTimestamp().ShouldBe(new DateTimeOffset(2026, 11, 3, 9, 0, 0, 123, TimeSpan.Zero));
        value.AsTimestamp().Offset.ShouldBe(TimeSpan.Zero);
        Read("""{"k":"ts","v":"2026-11-03T11:00:00.1234567+02:00"}""").AsTimestamp()
            .ShouldBe(new DateTimeOffset(2026, 11, 3, 9, 0, 0, 123, TimeSpan.Zero));
    }

    [Fact]
    public void K_U_060_WpValue_enum_code_validation()
    {
        foreach (var code in new[] { "Open", "", "open-door", "1open", "open door", null })
        {
            Should.Throw<ArgumentException>(() => WpValue.Enum(code!), code);
        }

        WpValue.Enum("doorOpen2").AsEnum().ShouldBe("doorOpen2");
        Should.Throw<JsonException>(() => Read("""{"k":"enum","v":"Open"}"""));
    }

    [Fact]
    public void K_U_061_WpValue_size_and_depth_limits()
    {
        var big = new string('x', 17 * 1024);
        Should.Throw<JsonException>(() => Read(Write(WpValue.String(big))));
        Read(Write(WpValue.String(new string('x', WpValue.MaxStringBytes)))).AsString().Length.ShouldBe(WpValue.MaxStringBytes);

        var many = WpValue.List([.. Enumerable.Range(0, 1001).Select(i => WpValue.Int(i))]);
        Should.Throw<JsonException>(() => Read(Write(many)));
        Read(Write(WpValue.List([.. Enumerable.Range(0, 1000).Select(i => WpValue.Int(i))]))).AsList().Count.ShouldBe(1000);

        Should.Throw<JsonException>(() => Read(Write(Nested(9))));
        Read(Write(Nested(8))).ShouldBe(Nested(8));

        var bigObject = Object("{\"a\":\"" + new string('x', 17 * 1024) + "\"}");
        Should.Throw<JsonException>(() => Read(Write(bigObject)));

        var deepObject = Object(Deep(8));
        Should.Throw<JsonException>(() => Read(Write(WpValue.List([deepObject]))));
        Read(Write(deepObject)).ShouldBe(deepObject);

        static WpValue Nested(int depth) => depth == 1 ? WpValue.Int(1) : WpValue.List([Nested(depth - 1)]);

        static string Deep(int depth) => depth == 0 ? "1" : "{\"a\":" + Deep(depth - 1) + "}";
    }

    [Fact]
    public void K_U_062_Compact_JSON_form_accepted_only_with_a_target_hint()
    {
        using var doc = JsonDocument.Parse("""[21.5, "21.50", 5, true, "open", "2026-11-03T10:00:00+01:00", "PT10S", [48.85, 2.35], {"a":1}, null, "Open", 1.5]""");
        var e = doc.RootElement;

        WpValue.ReadCompact(e[0], WpValueKind.Decimal).Value.ShouldBe(WpValue.Decimal(21.5m));
        WpValue.ReadCompact(e[1], WpValueKind.Decimal).Value.ShouldBe(WpValue.Decimal(21.5m));
        WpValue.ReadCompact(e[2], WpValueKind.Int).Value.ShouldBe(WpValue.Int(5));
        WpValue.ReadCompact(e[3], WpValueKind.Bool).Value.ShouldBe(WpValue.Bool(true));
        WpValue.ReadCompact(e[4], WpValueKind.Enum).Value.ShouldBe(WpValue.Enum("open"));
        WpValue.ReadCompact(e[4], WpValueKind.String).Value.ShouldBe(WpValue.String("open"));
        WpValue.ReadCompact(e[5], WpValueKind.Timestamp).Value.ShouldBe(WpValue.Timestamp(_at));
        WpValue.ReadCompact(e[6], WpValueKind.Duration).Value.ShouldBe(WpValue.Duration(TimeSpan.FromSeconds(10)));
        WpValue.ReadCompact(e[7], WpValueKind.Geo).Value.ShouldBe(WpValue.Geo(48.85, 2.35));
        WpValue.ReadCompact(e[8], WpValueKind.Object).Value.ShouldBe(Object("""{"a":1}"""));
        WpValue.ReadCompact(e[9], WpValueKind.Int).Value.ShouldBe(WpValue.Null);

        var noHint = WpValue.ReadCompact(e[0], null);
        noHint.IsSuccess.ShouldBeFalse();
        noHint.Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);

        WpValue.ReadCompact(e[10], WpValueKind.Enum).Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);
        WpValue.ReadCompact(e[11], WpValueKind.Int).Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);
        WpValue.ReadCompact(e[0], WpValueKind.String).Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);
        WpValue.ReadCompact(e[7], WpValueKind.List).Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);

        using var big = JsonDocument.Parse("\"" + new string('x', 17 * 1024) + "\"");
        WpValue.ReadCompact(big.RootElement, WpValueKind.String).Error.Code.ShouldBe(CommonErrorCodes.ValueTooLong);
    }

    private static WpValue[] AllKinds(JsonElement obj) =>
    [
        WpValue.Null, WpValue.Bool(false), WpValue.Int(long.MinValue), WpValue.Decimal(-0.000123456789012345678901m),
        WpValue.Enum("open"), WpValue.String("المدخل \"quoted\" \u0001"), WpValue.Timestamp(_at),
        WpValue.Duration(new TimeSpan(1, 2, 3, 4, 5)), WpValue.Geo(-33.86, 151.2, 12.5),
        WpValue.List([WpValue.Int(1), WpValue.List([WpValue.Null, WpValue.Enum("closed")])]), WpValue.Object(obj),
    ];

    private static List<(WpValueKind Kind, Action Get, Func<bool> TryGet)> Accessors(WpValue v) =>
    [
        (WpValueKind.Bool, () => v.AsBool(), () => v.TryAsBool(out _)),
        (WpValueKind.Int, () => v.AsInt(), () => v.TryAsInt(out _)),
        (WpValueKind.Decimal, () => v.AsDecimal(), () => v.TryAsDecimal(out _)),
        (WpValueKind.Enum, () => v.AsEnum(), () => v.TryAsEnum(out _)),
        (WpValueKind.String, () => v.AsString(), () => v.TryAsString(out _)),
        (WpValueKind.Timestamp, () => v.AsTimestamp(), () => v.TryAsTimestamp(out _)),
        (WpValueKind.Duration, () => v.AsDuration(), () => v.TryAsDuration(out _)),
        (WpValueKind.Geo, () => v.AsGeo(), () => v.TryAsGeo(out _)),
        (WpValueKind.List, () => v.AsList(), () => v.TryAsList(out _)),
        (WpValueKind.Object, () => v.AsObject(), () => v.TryAsObject(out _)),
    ];

    private static WpValue Object(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return WpValue.Object(doc.RootElement);
    }

    private static string Write(WpValue value) => JsonSerializer.Serialize(value, _options);

    private static WpValue Read(string json) => JsonSerializer.Deserialize<WpValue>(json, _options);
}

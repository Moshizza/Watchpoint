using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Xunit;

namespace Watchpoint.Kernel.Tests.Ids;

public sealed class EntityIdTests
{
    private const string UpperId = "0192F3A4-5B6C-7D8E-9FA0-B1C2D3E4F506";
    private const string LowerId = "0192f3a4-5b6c-7d8e-9fa0-b1c2d3e4f506";

    private sealed record Holder(DeviceId Id);

    private static readonly JsonSerializerOptions s_web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void K_U_001_New_creates_version_7_uuids()
    {
        var a = DeviceId.New();
        var b = DeviceId.New();

        a.ShouldNotBe(b);
        a.Value.Version.ShouldBe(7);
        b.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void K_U_002_Ids_sort_by_creation_time()
    {
        // Version 7 UUIDs carry a millisecond timestamp; ids created within the same millisecond
        // have no defined order, so create the second id once the clock has moved on (no sleep).
        var first = DeviceId.New();
        var second = DeviceId.New();
        for (var i = 0; i < 10_000_000 && Timestamp(second) == Timestamp(first); i++)
        {
            second = DeviceId.New();
        }

        Timestamp(second).ShouldBeGreaterThan(Timestamp(first));
        first.CompareTo(second).ShouldBeLessThan(0);
        (first < second).ShouldBeTrue();
        second.CompareTo(first).ShouldBeGreaterThan(0);

        // Same check with explicit timestamps.
        var t1 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var early = DeviceId.From(Guid.CreateVersion7(t1));
        var late = DeviceId.From(Guid.CreateVersion7(t1.AddMilliseconds(1)));
        early.CompareTo(late).ShouldBeLessThan(0);
    }

    [Fact]
    public void K_U_003_From_empty_guid_throws() =>
        Should.Throw<ArgumentException>(() => DeviceId.From(Guid.Empty));

    [Fact]
    public void K_U_004_ToString_is_lowercase_D_format()
    {
        var id = DeviceId.From(Guid.Parse(UpperId));

        id.ToString().ShouldBe(LowerId);
        $"{id}".ShouldBe(LowerId);
        id.ToString(null, null).ShouldBe(LowerId);

        Span<char> buffer = stackalloc char[40];
        id.TryFormat(buffer, out var written, default, null).ShouldBeTrue();
        buffer[..written].ToString().ShouldBe(LowerId);
    }

    [Fact]
    public void K_U_005_TryParse_accepts_upper_and_lower_case()
    {
        DeviceId.TryParse(UpperId, out var upper).ShouldBeTrue();
        DeviceId.TryParse(LowerId, out var lower).ShouldBeTrue();

        upper.ShouldBe(lower);
        upper.Value.ShouldBe(Guid.Parse(LowerId));
    }

    [Theory]
    [InlineData("{0192f3a4-5b6c-7d8e-9fa0-b1c2d3e4f506}")]
    [InlineData("(0192f3a4-5b6c-7d8e-9fa0-b1c2d3e4f506)")]
    [InlineData("0192f3a45b6c7d8e9fa0b1c2d3e4f506")]
    [InlineData(" 0192f3a4-5b6c-7d8e-9fa0-b1c2d3e4f506")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("x")]
    [InlineData("not-a-uuid-at-all-not-a-uuid-at-all!")]
    public void K_U_006_TryParse_rejects_other_formats(string? text)
    {
        DeviceId.TryParse(text, out var id).ShouldBeFalse();
        id.ShouldBe(default);
    }

    [Fact]
    public void K_U_007_Json_round_trip()
    {
        var holder = new Holder(DeviceId.From(Guid.Parse(UpperId)));

        var json = JsonSerializer.Serialize(holder, s_web);
        json.ShouldBe($$"""{"id":"{{LowerId}}"}""");
        JsonSerializer.Deserialize<Holder>(json, s_web).ShouldBe(holder);

        var map = new Dictionary<DeviceId, int> { [holder.Id] = 1 };
        var mapJson = JsonSerializer.Serialize(map, s_web);
        mapJson.ShouldBe($$"""{"{{LowerId}}":1}""");
        JsonSerializer.Deserialize<Dictionary<DeviceId, int>>(mapJson, s_web)!.ShouldContainKey(holder.Id);
    }

    [Theory]
    [InlineData("""{"id":"x"}""")]
    [InlineData("""{"id":"{0192f3a4-5b6c-7d8e-9fa0-b1c2d3e4f506}"}""")]
    [InlineData("""{"id":42}""")]
    public void K_U_008_Json_rejects_invalid_id(string json)
    {
        var ex = Should.Throw<JsonException>(() => JsonSerializer.Deserialize<Holder>(json, s_web));

        ex.Path.ShouldBe("$.id");
    }

    [Fact]
    public void K_U_009_Different_id_types_are_not_interchangeable()
    {
        const string Ok = """
            using Watchpoint.Kernel.Ids;
            internal static class Snippet
            {
                private static void Take(DeviceId id) { }
                internal static void Call() => Take(DeviceId.New());
            }
            """;
        const string Mixed = """
            using Watchpoint.Kernel.Ids;
            internal static class Snippet
            {
                private static void Take(DeviceId id) { }
                internal static void Call() => Take(ServerId.New());
            }
            """;

        Compile(Ok).ShouldBeEmpty();
        Compile(Mixed).ShouldContain("CS1503");
    }

    [Fact]
    public void K_U_010_ToLogString_uses_prefix()
    {
        DeviceId.From(Guid.Parse(UpperId)).ToLogString().ShouldBe("dev:" + LowerId);
        ServerId.From(Guid.Parse(UpperId)).ToLogString().ShouldBe("srv:" + LowerId);
    }

    private static long Timestamp(DeviceId id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.Value.TryWriteBytes(bytes, bigEndian: true, out _);
        return ((long)bytes[0] << 40) | ((long)bytes[1] << 32) | ((long)bytes[2] << 24)
            | ((long)bytes[3] << 16) | ((long)bytes[4] << 8) | bytes[5];
    }

    private static List<string> Compile(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Append(typeof(DeviceId).Assembly.Location)
            .Distinct()
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            "IdSnippet",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        return [.. compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.Id)];
    }
}

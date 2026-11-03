using System.Text;
using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Kernel.Tests.Paging;

public sealed class CursorCodecTests
{
    private static readonly Guid _id = Guid.Parse("0192f3a4-5b6c-7d8e-9f01-23456789abcd");

    [Fact]
    public void K_U_031_Cursor_round_trip()
    {
        var cursor = CursorCodec.Encode(1, ["Lobby", 3], _id);

        cursor.ShouldNotContain("=");
        cursor.ShouldNotContain("+");
        cursor.ShouldNotContain("/");

        var decoded = CursorCodec.Decode(cursor, 1).Value;
        decoded.LastId.ShouldBe(_id);
        decoded.KeyValues.Count.ShouldBe(2);
        decoded.KeyValues[0].GetString().ShouldBe("Lobby");
        decoded.KeyValues[1].GetInt32().ShouldBe(3);

        var at = new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(1));
        var mixed = CursorCodec.Decode(CursorCodec.Encode(4, [null, true, 2.5m, at], _id), 4).Value;
        mixed.KeyValues[0].ValueKind.ShouldBe(JsonValueKind.Null);
        mixed.KeyValues[1].GetBoolean().ShouldBeTrue();
        mixed.KeyValues[2].GetDecimal().ShouldBe(2.5m);
        mixed.KeyValues[3].GetString().ShouldBe("2026-11-03T09:00:00.000Z");

        Should.Throw<ArgumentException>(() => CursorCodec.Encode(1, [new object()], _id));
    }

    [Fact]
    public void K_U_032_Cursor_version_mismatch()
    {
        var cursor = CursorCodec.Encode(1, ["Lobby", 3], _id);

        var result = CursorCodec.Decode(cursor, 2);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(CommonErrorCodes.CursorInvalid);
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void K_U_033_Cursor_garbage_too_long_too_deep()
    {
        var random = new Random(42);
        var bytes = new byte[48];
        random.NextBytes(bytes);
        var garbage = Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var nested = "{\"v\":1,\"k\":[" + new string('[', 8) + "1" + new string(']', 8) + "],\"id\":\"" + _id + "\"}";
        var tooDeep = Base64Url(nested);
        var depthEight = "{\"v\":1,\"k\":[" + new string('[', 6) + "1" + new string(']', 6) + "],\"id\":\"" + _id + "\"}";

        string[] invalid =
        [
            garbage,
            new string('A', 3 * 1024),
            tooDeep,
            "",
            "not base64!",
            Base64Url("[1]"),
            Base64Url("{\"v\":1,\"k\":[],\"id\":\"x\"}"),
            Base64Url("{\"v\":1,\"k\":[],\"id\":\"" + _id + "\",\"x\":1}"),
            Base64Url("{\"v\":1,\"k\":[]}"),
        ];

        foreach (var cursor in invalid)
        {
            CursorCodec.Decode(cursor, 1).Error.Code.ShouldBe(CommonErrorCodes.CursorInvalid, cursor);
        }

        CursorCodec.Decode(Base64Url(depthEight), 1).IsSuccess.ShouldBeTrue();
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

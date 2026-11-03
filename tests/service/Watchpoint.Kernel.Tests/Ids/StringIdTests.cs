using System.Text.Json;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Xunit;

namespace Watchpoint.Kernel.Tests.Ids;

public sealed class StringIdTests
{
    [Theory]
    [InlineData("ac.door")]
    [InlineData("vendor.salto.hotel-room")]
    [InlineData("a1.b2.c3.d4.e5")]
    public void K_U_011_TypeId_Parse_accepts_valid_values(string text)
    {
        var result = TypeId.Parse(text);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldBe(text);
        result.Value.ToString().ShouldBe(text);
    }

    [Theory]
    [InlineData("Ac.Door")]
    [InlineData("door")]
    [InlineData("ac..door")]
    [InlineData("ac.door.")]
    [InlineData("1ac.door")]
    [InlineData("ac.door\n")]
    [InlineData("a.b.c.d.e.f")]
    [InlineData("")]
    [InlineData(null)]
    public void K_U_012_TypeId_Parse_rejects_invalid_values(string? text) =>
        ShouldBeInvalidFormat(TypeId.Parse(text));

    [Fact]
    public void K_U_012_TypeId_Parse_rejects_values_longer_than_100_characters()
    {
        var hundred = "ac." + new string('a', 97);
        TypeId.Parse(hundred).IsSuccess.ShouldBeTrue();
        ShouldBeInvalidFormat(TypeId.Parse(hundred + "a"));
    }

    [Theory]
    [InlineData("doorPosition", true)]
    [InlineData("vendor.saltoDnd", true)]
    [InlineData("a", true)]
    [InlineData("DoorPosition", false)]
    [InlineData("door-position", false)]
    [InlineData("vendor.salto.dnd", false)]
    [InlineData("vendor.", false)]
    [InlineData("", false)]
    public void K_U_013_CapabilityId_pattern(string text, bool valid) =>
        Check(CapabilityId.Parse(text), valid);

    [Theory]
    [InlineData("position", true)]
    [InlineData("gotoPreset", true)]
    [InlineData("x1", true)]
    [InlineData("Position", false)]
    [InlineData("goto.preset", false)]
    [InlineData("goto_preset", false)]
    [InlineData("1x", false)]
    public void K_U_013_MemberId_pattern(string text, bool valid) =>
        Check(MemberId.Parse(text), valid);

    [Fact]
    public void K_U_013_CapabilityId_and_MemberId_max_length_is_64()
    {
        CapabilityId.Parse(new string('a', 64)).IsSuccess.ShouldBeTrue();
        CapabilityId.Parse(new string('a', 65)).IsSuccess.ShouldBeFalse();
        MemberId.Parse(new string('a', 64)).IsSuccess.ShouldBeTrue();
        MemberId.Parse(new string('a', 65)).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("watchpoint.integration.salto", true)]
    [InlineData("vendor.my-package", true)]
    [InlineData("a.b.c.d.e.f", true)]
    [InlineData("watchpoint", false)]
    [InlineData("Watchpoint.integration", false)]
    [InlineData("a.b.c.d.e.f.g", false)]
    [InlineData("vendor.myPackage", false)]
    public void K_U_014_PackageId_pattern(string text, bool valid) =>
        Check(PackageId.Parse(text), valid);

    [Theory]
    [InlineData("alarms.acknowledge", true)]
    [InlineData("commands.openable.open", true)]
    [InlineData("commands.openable.*", true)]
    [InlineData("alarms", false)]
    [InlineData("Alarms.acknowledge", false)]
    [InlineData("alarms.ack-nowledge", false)]
    [InlineData("alarms.*", true)]
    [InlineData("alarms.ack*", false)]
    [InlineData("*.open", false)]
    public void K_U_014_PermissionCode_pattern(string text, bool valid) =>
        Check(PermissionCode.Parse(text), valid);

    [Theory]
    [InlineData("registry.device.not_found", true)]
    [InlineData("common.internal", true)]
    [InlineData("x.y", true)]
    [InlineData("registry", false)]
    [InlineData("registry.Device.not_found", false)]
    [InlineData("registry.device.not-found", false)]
    [InlineData("a.b.c.d.e.f", false)]
    public void K_U_014_ErrorCode_pattern(string text, bool valid) =>
        Check(ErrorCode.Parse(text), valid);

    [Fact]
    public void K_U_014_String_ids_max_length_is_128()
    {
        var at128 = "a." + new string('b', 126);
        PackageId.Parse(at128).IsSuccess.ShouldBeTrue();
        PackageId.Parse(at128 + "b").IsSuccess.ShouldBeFalse();
        PermissionCode.Parse(at128).IsSuccess.ShouldBeTrue();
        PermissionCode.Parse(at128 + "b").IsSuccess.ShouldBeFalse();
        ErrorCode.Parse(at128).IsSuccess.ShouldBeTrue();
        ErrorCode.Parse(at128 + "b").IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void K_U_014_String_ids_use_ordinal_equality_and_json_strings()
    {
        TypeId.FromTrusted("ac.door").ShouldBe(TypeId.Parse("ac.door").Value);
        PermissionCode.FromTrusted("a.b").ShouldNotBe(PermissionCode.FromTrusted("a.B"));

        var json = JsonSerializer.Serialize(TypeId.FromTrusted("ac.door"));
        json.ShouldBe("\"ac.door\"");
        JsonSerializer.Deserialize<TypeId>(json).ShouldBe(TypeId.FromTrusted("ac.door"));
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<TypeId>("\"Door\""));
    }

    [Fact]
    public void K_U_015_PropertyPath_Parse()
    {
        var path = PropertyPath.Parse("doorPosition.position");
        path.IsSuccess.ShouldBeTrue();
        path.Value.Capability.ShouldBe(CapabilityId.FromTrusted("doorPosition"));
        path.Value.Member.ShouldBe(MemberId.FromTrusted("position"));
        path.Value.ToString().ShouldBe("doorPosition.position");

        var vendor = PropertyPath.Parse("vendor.saltoDnd.active");
        vendor.Value.Capability.Value.ShouldBe("vendor.saltoDnd");
        vendor.Value.Member.Value.ShouldBe("active");

        ShouldBeInvalidFormat(PropertyPath.Parse("position"));
        ShouldBeInvalidFormat(PropertyPath.Parse(".position"));
        ShouldBeInvalidFormat(PropertyPath.Parse("doorPosition."));
        ShouldBeInvalidFormat(PropertyPath.Parse(null));

        JsonSerializer.Serialize(path.Value).ShouldBe("\"doorPosition.position\"");
        JsonSerializer.Deserialize<PropertyPath>("\"doorPosition.position\"").ShouldBe(path.Value);
    }

    private static void Check<T>(Result<T> result, bool valid)
    {
        if (valid)
        {
            result.IsSuccess.ShouldBeTrue();
        }
        else
        {
            ShouldBeInvalidFormat(result);
        }
    }

    private static void ShouldBeInvalidFormat<T>(Result<T> result)
    {
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Args.ShouldContainKey("expected");
    }
}

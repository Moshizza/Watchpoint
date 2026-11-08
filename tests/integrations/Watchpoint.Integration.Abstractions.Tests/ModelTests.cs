using Shouldly;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;
using Xunit;

namespace Watchpoint.Integration.Abstractions.Tests;

public sealed class ModelTests
{
    [Fact]
    public void INT_U_001_ConnectorMessage_Of_stores_key_and_args_and_rejects_disallowed_types()
    {
        var at = new DateTimeOffset(2026, 11, 7, 9, 0, 0, TimeSpan.Zero);
        var message = ConnectorMessage.Of("errors.loginRefused", ("user", "admin"), ("attempts", 3), ("at", at));

        message.Key.ShouldBe("errors.loginRefused");
        message.Args.Count.ShouldBe(3);
        message.Args["user"].ShouldBe("admin");
        message.Args["attempts"].ShouldBe(3);
        message.Args["at"].ShouldBe(at);
        ConnectorMessage.Of("connected").Args.ShouldBeEmpty();
        message.ShouldBe(ConnectorMessage.Of("errors.loginRefused", ("user", "admin"), ("attempts", 3), ("at", at)));
        message.ToMessage("acme.access").Key.ShouldBe("packages.acme.access.errors.loginRefused");
        var id = DeviceId.New();
        ConnectorMessage.Of("errors.device", ("device", id), ("codes", (IReadOnlyList<string>)["a"])).Args["device"].ShouldBe(id);

        Should.Throw<ArgumentException>(() => ConnectorMessage.Of("errors.x", ("value", new object())));
        Should.Throw<ArgumentException>(() => ConnectorMessage.Of("errors.x", ("value", WpValue.Int(1))));
        Should.Throw<ArgumentException>(() => ConnectorMessage.Of("errors.x", ("Value", 1)));
        Should.Throw<ArgumentException>(() => ConnectorMessage.Of("errors.x", ("a", 1), ("a", 2)));
        Should.Throw<ArgumentException>(() => ConnectorMessage.Of("Errors.X"));
        Should.Throw<ArgumentException>(() => ConnectorMessage.Of(""));
        Should.Throw<ArgumentException>(() => new ConnectorMessage("errors.x", new Dictionary<string, object?> { ["list"] = new List<int>() }));
    }

    [Fact]
    public void INT_U_002_ConnectorCommandResult_factories_are_consistent()
    {
        var ok = ConnectorCommandResult.Succeeded();
        ok.Outcome.ShouldBe(CommandOutcome.Succeeded);
        ok.Failure.ShouldBeNull();
        ok.Message.ShouldBeNull();

        var unsupported = ConnectorCommandResult.NotSupported();
        unsupported.Outcome.ShouldBe(CommandOutcome.NotSupported);
        unsupported.Failure.ShouldBeNull();

        var failed = ConnectorCommandResult.Failed(FailureCategory.DeviceOffline, ConnectorMessage.Of("errors.offline"), "E42");
        failed.Outcome.ShouldBe(CommandOutcome.Failed);
        failed.Failure.ShouldBe(FailureCategory.DeviceOffline);
        failed.Message!.Key.ShouldBe("errors.offline");
        failed.VendorCode.ShouldBe("E42");

        Should.Throw<ArgumentNullException>(() => ConnectorCommandResult.Failed(FailureCategory.Timeout, null!));
        Should.Throw<ArgumentException>(() => new ConnectorCommandResult(CommandOutcome.Failed, null, ConnectorMessage.Of("errors.x"), null, null));
        Should.Throw<ArgumentException>(() => new ConnectorCommandResult(CommandOutcome.Failed, FailureCategory.Timeout, null, null, null));
        Should.Throw<ArgumentException>(() => new ConnectorCommandResult(CommandOutcome.Succeeded, FailureCategory.Timeout, null, null, null));
    }

    [Fact]
    public void INT_U_003_DeviceKey_equality_and_hashing()
    {
        var a = new DeviceKey("srv-1", "door-7");
        var b = new DeviceKey("srv-1", "door-7");

        a.ShouldBe(b);
        (a == b).ShouldBeTrue();
        a.GetHashCode().ShouldBe(b.GetHashCode());
        a.ShouldNotBe(new DeviceKey("srv-1", "DOOR-7"));
        a.ShouldNotBe(new DeviceKey("srv-2", "door-7"));
        a.ShouldNotBe(new DeviceKey("srv-1door", "-7"));
        new HashSet<DeviceKey> { a, b, new("srv-1", "door-8") }.Count.ShouldBe(2);
        a.ToString().ShouldBe("srv-1/door-7");
        Should.Throw<ArgumentNullException>(() => new DeviceKey(null!, "x"));
        Should.Throw<ArgumentNullException>(() => new DeviceKey("x", null!));
    }

    [Fact]
    public void INT_U_004_InventorySnapshot_validation_rejects_bad_native_ids()
    {
        Valid().Validate().IsSuccess.ShouldBeTrue();
        (Valid() with { IsComplete = false, Servers = [] }).Validate().IsSuccess.ShouldBeTrue();

        Paths(Valid() with { Servers = [] }).ShouldContain("/servers");
        Paths(Valid() with { Servers = [Server(""), Server("root")] }).ShouldBe(["/servers/0/nativeId"]);
        Paths(Valid() with { Servers = [Server("root"), Server("root")] }).ShouldBe(["/servers/1/nativeId"]);
        Paths(Valid() with { Devices = [Device("root", new string('x', 257))] }).ShouldBe(["/devices/0/nativeId"]);
        Paths(Valid() with { Devices = [Device("root", new string('x', 256))] }).ShouldBeEmpty();
        Paths(Valid() with { Devices = [Device("root", "d1"), Device("root", "d1")] }).ShouldBe(["/devices/1/nativeId"]);
        Paths(Valid() with { Servers = [Server("root"), Server("sub")], Devices = [Device("root", "d1"), Device("sub", "d1")] })
            .ShouldBeEmpty();
        Paths(Valid() with { Devices = [Device("other", "d1")] }).ShouldBe(["/devices/0/serverNativeId"]);
        Paths(Valid() with { Devices = [Device(" ", "d1")] }).ShouldBe(["/devices/0/serverNativeId"]);

        var error = (Valid() with { Devices = [Device("root", "")] }).Validate().Error;
        error.Code.ShouldBe(CommonErrorCodes.ValidationFailed);
        error.FieldErrors.Single().Code.ShouldBe(CommonErrorCodes.ValueRequired);
    }

    private static List<string> Paths(InventorySnapshot snapshot)
    {
        var result = snapshot.Validate();
        return result.IsSuccess ? [] : [.. result.Error.FieldErrors.Select(f => f.Path)];
    }

    private static InventorySnapshot Valid() => new([Server("root")], [Device("root", "d1"), Device("root", "d2")], IsComplete: true);

    private static InventoryServerItem Server(string id) =>
        new(id, null, "acme.server", "1.0", "Server " + id, new Dictionary<string, WpValue>());

    private static InventoryDeviceItem Device(string server, string id) =>
        new(id, server, null, "acme.door", "1.0", "Door " + id, new Dictionary<string, WpValue>(), [], []);
}

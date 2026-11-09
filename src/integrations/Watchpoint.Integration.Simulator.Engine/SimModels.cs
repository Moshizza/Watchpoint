namespace Watchpoint.Integration.Simulator.Engine;

public sealed record SimServer(
    string NativeId,
    string? ParentNativeId,
    string TypeId,
    string TypeVersion,
    string VendorName,
    IReadOnlyDictionary<string, object?> VendorAttributes);

public sealed record SimLink(
    string Kind,
    string ToNativeId,
    string? ToServerNativeId = null);

public sealed record SimDevice(
    string NativeId,
    string ServerNativeId,
    string? ParentNativeId,
    string TypeId,
    string TypeVersion,
    string VendorName,
    IReadOnlyDictionary<string, object?> VendorAttributes,
    IReadOnlyList<string> UnsupportedOptionalCapabilities,
    IReadOnlyList<SimLink> Links);

public sealed record SimInventory(
    IReadOnlyList<SimServer> Servers,
    IReadOnlyList<SimDevice> Devices,
    bool IsComplete = true);

public sealed record SimEventRecord(
    ulong Seq,
    string Device,
    string Event,
    IReadOnlyDictionary<string, object?> Payload,
    DateTimeOffset At);

public sealed record SimChangeRecord(
    ulong Seq,
    string Device,
    IReadOnlyDictionary<string, object?> Props,
    DateTimeOffset At);

public sealed record SimHealthRecord(
    string NativeId,
    bool IsServer,
    string Connectivity,
    IReadOnlyList<string>? Faults,
    string? Reason,
    DateTimeOffset At);

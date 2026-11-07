namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>Identifies a device of a connection by its native ids (INT §7.1). Comparison is ordinal (case-sensitive).</summary>
/// <param name="ServerNativeId">The native id of the device's server.</param>
/// <param name="NativeId">The native id of the device, unique per server.</param>
public readonly record struct DeviceKey(string ServerNativeId, string NativeId)
{
    /// <summary>Gets the native id of the device's server.</summary>
    public string ServerNativeId { get; init; } = ServerNativeId ?? throw new ArgumentNullException(nameof(ServerNativeId));

    /// <summary>Gets the native id of the device.</summary>
    public string NativeId { get; init; } = NativeId ?? throw new ArgumentNullException(nameof(NativeId));

    /// <inheritdoc/>
    public override string ToString() => ServerNativeId + "/" + NativeId;
}

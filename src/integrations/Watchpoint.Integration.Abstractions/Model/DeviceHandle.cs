using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>An imported, enabled device of the connection as the connector sees it (INT §7.2).</summary>
/// <param name="Key">The device's native key.</param>
/// <param name="TypeId">The device type id.</param>
/// <param name="TypeVersion">The device type version.</param>
/// <param name="Attributes">Vendor attributes, possibly edited by administrators.</param>
/// <param name="UnsupportedOptionalCapabilities">Optional capabilities the device lacks.</param>
public sealed record DeviceHandle(DeviceKey Key, string TypeId, string TypeVersion,
    IReadOnlyDictionary<string, WpValue> Attributes, IReadOnlyList<string> UnsupportedOptionalCapabilities);

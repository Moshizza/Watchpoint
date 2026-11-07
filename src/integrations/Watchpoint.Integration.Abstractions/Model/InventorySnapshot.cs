using System.Globalization;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Abstractions.Model;

/// <summary>
/// The servers and devices a connector knows (INT §5). <see cref="IsComplete"/> is false when only part of the system
/// could be read: the registry then orphans nothing (REG §8).
/// </summary>
/// <param name="Servers">The servers (at least the root server in a complete snapshot).</param>
/// <param name="Devices">The devices.</param>
/// <param name="IsComplete">Whether this is the whole system.</param>
public sealed record InventorySnapshot(IReadOnlyList<InventoryServerItem> Servers, IReadOnlyList<InventoryDeviceItem> Devices,
    bool IsComplete)
{
    /// <summary>Maximum length of a native id.</summary>
    public const int MaxNativeIdLength = 256;

    /// <summary>
    /// Checks the native id rules: not empty, at most <see cref="MaxNativeIdLength"/> characters, server ids unique,
    /// device ids unique per server; a complete snapshot has at least one server and its devices reference listed servers.
    /// </summary>
    /// <returns>Success, or <c>common.validation.failed</c> with JSON-pointer field errors (e.g. <c>/devices/3/nativeId</c>).</returns>
    public Result Validate()
    {
        var errors = new List<FieldError>();
        if (IsComplete && Servers.Count == 0)
        {
            errors.Add(CommonErrors.RequiredAt("/servers"));
        }

        var servers = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Servers.Count; i++)
        {
            var path = Path("servers", i);
            if (CheckId(errors, path, Servers[i].NativeId) && !servers.Add(Servers[i].NativeId))
            {
                errors.Add(CommonErrors.DuplicateAt(path, Servers[i].NativeId));
            }
        }

        var devices = new HashSet<DeviceKey>();
        for (var i = 0; i < Devices.Count; i++)
        {
            var device = Devices[i];
            var path = Path("devices", i);
            var serverKnown = CheckId(errors, Path("devices", i, "serverNativeId"), device.ServerNativeId);
            if (serverKnown && IsComplete && !servers.Contains(device.ServerNativeId))
            {
                errors.Add(CommonErrors.NotAllowedAt(Path("devices", i, "serverNativeId"), [.. servers]));
            }

            if (CheckId(errors, path, device.NativeId) && serverKnown && !devices.Add(new DeviceKey(device.ServerNativeId, device.NativeId)))
            {
                errors.Add(CommonErrors.DuplicateAt(path, device.NativeId));
            }
        }

        return errors.Count == 0 ? Result.Success() : Result.Failure(CommonErrors.ValidationFailed(errors));
    }

    private static bool CheckId(List<FieldError> errors, string path, string? id)
    {
        var idPath = path.EndsWith("NativeId", StringComparison.Ordinal) ? path : path + "/nativeId";
        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add(CommonErrors.RequiredAt(idPath));
            return false;
        }

        if (id.Length > MaxNativeIdLength)
        {
            errors.Add(CommonErrors.TooLongAt(idPath, MaxNativeIdLength));
            return false;
        }

        return true;
    }

    private static string Path(string list, int index, string? field = null) =>
        string.Create(CultureInfo.InvariantCulture, $"/{list}/{index}{(field is null ? string.Empty : "/" + field)}");
}

/// <summary>A server of the integrated system (the root server has no parent).</summary>
/// <param name="NativeId">The logical vendor id (never a display name), stable across restarts.</param>
/// <param name="ParentNativeId">The parent server, if any.</param>
/// <param name="TypeId">The server type id.</param>
/// <param name="TypeVersion">The server type version.</param>
/// <param name="VendorName">The name in the vendor system.</param>
/// <param name="VendorAttributes">Vendor attributes.</param>
public sealed record InventoryServerItem(string NativeId, string? ParentNativeId, string TypeId, string TypeVersion,
    string VendorName, IReadOnlyDictionary<string, WpValue> VendorAttributes);

/// <summary>A device of the integrated system.</summary>
/// <param name="NativeId">The logical vendor id, unique per server, stable across restarts.</param>
/// <param name="ServerNativeId">The server the device belongs to.</param>
/// <param name="ParentNativeId">The parent device, if any.</param>
/// <param name="TypeId">The device type id.</param>
/// <param name="TypeVersion">The device type version.</param>
/// <param name="VendorName">The name in the vendor system.</param>
/// <param name="VendorAttributes">Vendor attributes.</param>
/// <param name="UnsupportedOptionalCapabilities">Optional capabilities of the type this device lacks.</param>
/// <param name="Links">Links to other devices.</param>
public sealed record InventoryDeviceItem(string NativeId, string ServerNativeId, string? ParentNativeId,
    string TypeId, string TypeVersion, string VendorName, IReadOnlyDictionary<string, WpValue> VendorAttributes,
    IReadOnlyList<string> UnsupportedOptionalCapabilities, IReadOnlyList<InventoryLinkItem> Links);

/// <summary>A link from a device to another device.</summary>
/// <param name="Kind">The link kind: <c>cameraViews</c>, <c>memberOf</c>, <c>controls</c>, <c>powers</c>, <c>partOf</c>.</param>
/// <param name="ToNativeId">The target device's native id.</param>
/// <param name="ToServerNativeId">The target's server, when different from the source's.</param>
public sealed record InventoryLinkItem(string Kind, string ToNativeId, string? ToServerNativeId);

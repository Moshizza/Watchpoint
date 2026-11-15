using System.ComponentModel.DataAnnotations.Schema;

namespace Watchpoint.Registry.Internal.Storage;

[Table("sites", Schema = "wp_registry")]
internal sealed class SiteRecord
{
    public Guid Id { get; set; }
    public string NameDefault { get; set; } = null!;
    public string NameTranslations { get; set; } = "{}";
    public string DefaultTimezone { get; set; } = null!;
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("discovered_items", Schema = "wp_registry")]
internal sealed class DiscoveredItemRecord
{
    public Guid ConnectionId { get; set; }
    public string NativeId { get; set; } = null!;
    public string Kind { get; set; } = "device";
    public string ServerNativeId { get; set; } = null!;
    public string? ParentNativeId { get; set; }
    public string TypeId { get; set; } = null!;
    public string TypeVersion { get; set; } = null!;
    public string VendorName { get; set; } = null!;
    public string VendorAttributes { get; set; } = "{}";
    public string Status { get; set; } = "new";
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

[Table("locations", Schema = "wp_registry")]
internal sealed class LocationRecord
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string NameDefault { get; set; } = null!;
    public string NameTranslations { get; set; } = "{}";
    public string Kind { get; set; } = null!;
    public string Path { get; set; } = null!;
    public int Depth { get; set; }
    public string? Tz { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public int SortOrder { get; set; }
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("tag_families", Schema = "wp_registry")]
internal sealed class TagFamilyRecord
{
    public Guid Id { get; set; }
    public string Key { get; set; } = null!;
    public string NameDefault { get; set; } = null!;
    public string NameTranslations { get; set; } = "{}";
    public string? Color { get; set; }
    public string Cardinality { get; set; } = null!;
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("tags", Schema = "wp_registry")]
internal sealed class TagRecord
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public string Key { get; set; } = null!;
    public string NameDefault { get; set; } = null!;
    public string NameTranslations { get; set; } = "{}";
    public string? Color { get; set; }
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("servers", Schema = "wp_registry")]
internal sealed class ServerRecord
{
    public Guid Id { get; set; }
    public string TypeId { get; set; } = null!;
    public string TypeVersion { get; set; } = null!;
    public Guid ConnectionId { get; set; }
    public Guid? ParentId { get; set; }
    public string NativeId { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string VendorName { get; set; } = null!;
    public Guid LocationId { get; set; }
    public Guid[] Tags { get; set; } = [];
    public string CustomFields { get; set; } = "{}";
    public bool Enabled { get; set; }
    public bool Maintenance { get; set; }
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("devices", Schema = "wp_registry")]
internal sealed class DeviceRecord
{
    public Guid Id { get; set; }
    public string TypeId { get; set; } = null!;
    public string TypeVersion { get; set; } = null!;
    public Guid ServerId { get; set; }
    public Guid? ParentId { get; set; }
    public string NativeId { get; set; } = null!;
    public string? DisplayName { get; set; }
    public string VendorName { get; set; } = null!;
    public Guid LocationId { get; set; }
    public string? Geometry { get; set; }
    public string Attributes { get; set; } = "{}";
    public Guid[] Tags { get; set; } = [];
    public string CustomFields { get; set; } = "{}";
    public string[] UnsupportedCaps { get; set; } = [];
    public bool Enabled { get; set; }
    public bool Maintenance { get; set; }
    public bool Orphaned { get; set; }
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("device_links", Schema = "wp_registry")]
internal sealed class DeviceLinkRecord
{
    public Guid SourceDeviceId { get; set; }
    public string Role { get; set; } = null!;
    public Guid TargetDeviceId { get; set; }
    public string Metadata { get; set; } = "{}";
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

[Table("custom_fields", Schema = "wp_registry")]
internal sealed class CustomFieldRecord
{
    public Guid Id { get; set; }
    public string Target { get; set; } = null!;
    public string Key { get; set; } = null!;
    public string Label { get; set; } = null!;
    public string Kind { get; set; } = null!;
    public bool Required { get; set; }
    public string? DefaultValue { get; set; }
    public long RowVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>The single row holding the registry revision (spec REG §10, table <c>revision</c>).</summary>
[Table("revision", Schema = "wp_registry")]
internal sealed class RevisionRecord
{
    /// <summary>The key of the only row.</summary>
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;
    public long Value { get; set; }
}

/// <summary>One <c>registry.changed</c> record of the transactional outbox (spec REG §10, table <c>outbox</c>).</summary>
[Table("outbox", Schema = "wp_registry")]
internal sealed class OutboxRecord
{
    public long Revision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Actor { get; set; }
    public string? Reason { get; set; }
    public string? CorrelationId { get; set; }
    public string Changes { get; set; } = "[]";
    public DateTimeOffset? SentAt { get; set; }
}

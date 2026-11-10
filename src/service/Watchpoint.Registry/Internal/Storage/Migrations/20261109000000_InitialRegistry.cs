#pragma warning disable IDE0005

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;
using Watchpoint.Registry.Contracts;

#nullable disable

namespace Watchpoint.Registry.Internal.Storage.Migrations;

/// <inheritdoc />
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments")]
internal sealed partial class InitialRegistry : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "wp_registry");

        migrationBuilder.CreateTable(
            name: "sites",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name_default = table.Column<string>(type: "text", nullable: false),
                name_translations = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                default_timezone = table.Column<string>(type: "text", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_sites", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tag_families",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "text", nullable: false),
                name_default = table.Column<string>(type: "text", nullable: false),
                name_translations = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                color = table.Column<string>(type: "text", nullable: true),
                cardinality = table.Column<string>(type: "text", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tag_families", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "custom_fields",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                target = table.Column<string>(type: "text", nullable: false),
                key = table.Column<string>(type: "text", nullable: false),
                label = table.Column<string>(type: "jsonb", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                required = table.Column<bool>(type: "boolean", nullable: false),
                default_value = table.Column<string>(type: "jsonb", nullable: true),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_custom_fields", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "locations",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                name_default = table.Column<string>(type: "text", nullable: false),
                name_translations = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                path = table.Column<string>(type: "text", nullable: false),
                depth = table.Column<int>(type: "integer", nullable: false),
                tz = table.Column<string>(type: "text", nullable: true),
                address = table.Column<string>(type: "text", nullable: true),
                notes = table.Column<string>(type: "text", nullable: true),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_locations", x => x.id);
                table.ForeignKey(
                    name: "fk_locations_locations_parent_id",
                    column: x => x.parent_id,
                    principalSchema: "wp_registry",
                    principalTable: "locations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "tags",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                family_id = table.Column<Guid>(type: "uuid", nullable: false),
                key = table.Column<string>(type: "text", nullable: false),
                name_default = table.Column<string>(type: "text", nullable: false),
                name_translations = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                color = table.Column<string>(type: "text", nullable: true),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_tags", x => x.id);
                table.ForeignKey(
                    name: "fk_tags_tag_families_family_id",
                    column: x => x.family_id,
                    principalSchema: "wp_registry",
                    principalTable: "tag_families",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "servers",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                type_id = table.Column<string>(type: "text", nullable: false),
                type_version = table.Column<string>(type: "text", nullable: false),
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                native_id = table.Column<string>(type: "text", nullable: false),
                display_name = table.Column<string>(type: "text", nullable: true),
                vendor_name = table.Column<string>(type: "text", nullable: false),
                location_id = table.Column<Guid>(type: "uuid", nullable: false),
                tags = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                custom_fields = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                maintenance = table.Column<bool>(type: "boolean", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_servers", x => x.id);
                table.ForeignKey(
                    name: "fk_servers_locations_location_id",
                    column: x => x.location_id,
                    principalSchema: "wp_registry",
                    principalTable: "locations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_servers_servers_parent_id",
                    column: x => x.parent_id,
                    principalSchema: "wp_registry",
                    principalTable: "servers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "devices",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                type_id = table.Column<string>(type: "text", nullable: false),
                type_version = table.Column<string>(type: "text", nullable: false),
                server_id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: true),
                native_id = table.Column<string>(type: "text", nullable: false),
                display_name = table.Column<string>(type: "text", nullable: true),
                vendor_name = table.Column<string>(type: "text", nullable: false),
                location_id = table.Column<Guid>(type: "uuid", nullable: false),
                geometry = table.Column<DeviceGeometry>(type: "jsonb", nullable: true),
                tags = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                attributes = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                custom_fields = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                unsupported_caps = table.Column<string[]>(type: "text[]", nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                maintenance = table.Column<bool>(type: "boolean", nullable: false),
                orphaned = table.Column<bool>(type: "boolean", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_devices", x => x.id);
                table.ForeignKey(
                    name: "fk_devices_locations_location_id",
                    column: x => x.location_id,
                    principalSchema: "wp_registry",
                    principalTable: "locations",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_devices_devices_parent_id",
                    column: x => x.parent_id,
                    principalSchema: "wp_registry",
                    principalTable: "devices",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_devices_servers_server_id",
                    column: x => x.server_id,
                    principalSchema: "wp_registry",
                    principalTable: "servers",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "device_links",
            schema: "wp_registry",
            columns: table => new
            {
                source_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                target_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                metadata = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_device_links", x => new { x.source_device_id, x.role, x.target_device_id });
                table.ForeignKey(
                    name: "fk_device_links_devices_source_device_id",
                    column: x => x.source_device_id,
                    principalSchema: "wp_registry",
                    principalTable: "devices",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_device_links_devices_target_device_id",
                    column: x => x.target_device_id,
                    principalSchema: "wp_registry",
                    principalTable: "devices",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_custom_fields_target_key",
            schema: "wp_registry",
            table: "custom_fields",
            columns: new[] { "target", "key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_device_links_target_device_id",
            schema: "wp_registry",
            table: "device_links",
            column: "target_device_id");

        migrationBuilder.CreateIndex(
            name: "ix_devices_location_id",
            schema: "wp_registry",
            table: "devices",
            column: "location_id");

        migrationBuilder.CreateIndex(
            name: "ix_devices_server_id_native_id",
            schema: "wp_registry",
            table: "devices",
            columns: new[] { "server_id", "native_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_locations_parent_id",
            schema: "wp_registry",
            table: "locations",
            column: "parent_id");

        migrationBuilder.CreateIndex(
            name: "ix_locations_path",
            schema: "wp_registry",
            table: "locations",
            column: "path");

        migrationBuilder.CreateIndex(
            name: "ix_servers_connection_id_native_id",
            schema: "wp_registry",
            table: "servers",
            columns: new[] { "connection_id", "native_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_servers_location_id",
            schema: "wp_registry",
            table: "servers",
            column: "location_id");

        migrationBuilder.CreateIndex(
            name: "ix_servers_parent_id",
            schema: "wp_registry",
            table: "servers",
            column: "parent_id");

        migrationBuilder.CreateIndex(
            name: "ix_tag_families_key",
            schema: "wp_registry",
            table: "tag_families",
            column: "key",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_tags_family_id_key",
            schema: "wp_registry",
            table: "tags",
            columns: new[] { "family_id", "key" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "custom_fields",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "device_links",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "devices",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "servers",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "tags",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "locations",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "sites",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "tag_families",
            schema: "wp_registry");
    }
}

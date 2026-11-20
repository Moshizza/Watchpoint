#pragma warning disable IDE0005
#pragma warning disable CA1861

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchpoint.Registry.Internal.Storage.Migrations;

/// <summary>
/// Realigns the outbox and device link tables to REG §6, §10 (T1.07c, 2026-11 architecture review D1): the outbox's
/// <c>actor/reason/correlation_id</c> columns become <c>actor/origin/change_set/comment</c> (<c>ChangeContext(Actor,
/// Origin, ChangeSet, Comment)</c>), and device links get their own id and a closed <c>LinkKind</c> instead of a
/// composite key on a free-text role.
/// </summary>
/// <inheritdoc />
internal sealed partial class RealignOutboxAndDeviceLinks : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // outbox: actor/reason/correlation_id -> actor/origin/change_set/comment.
        migrationBuilder.DropColumn(name: "reason", schema: "wp_registry", table: "outbox");
        migrationBuilder.DropColumn(name: "correlation_id", schema: "wp_registry", table: "outbox");
        migrationBuilder.DropColumn(name: "actor", schema: "wp_registry", table: "outbox");

        migrationBuilder.AddColumn<Guid>(
            name: "actor",
            schema: "wp_registry",
            table: "outbox",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "origin",
            schema: "wp_registry",
            table: "outbox",
            type: "text",
            nullable: false,
            defaultValue: "api");

        migrationBuilder.AddColumn<Guid>(
            name: "change_set",
            schema: "wp_registry",
            table: "outbox",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "comment",
            schema: "wp_registry",
            table: "outbox",
            type: "text",
            nullable: true);

        // device_links: source_device_id/role/target_device_id/metadata -> id/kind/from_id/to_id.
        migrationBuilder.DropTable(name: "device_links", schema: "wp_registry");

        migrationBuilder.CreateTable(
            name: "device_links",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                from_id = table.Column<Guid>(type: "uuid", nullable: false),
                to_id = table.Column<Guid>(type: "uuid", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_device_links", x => x.id);
                table.ForeignKey(
                    name: "fk_device_links_devices_from_id",
                    column: x => x.from_id,
                    principalSchema: "wp_registry",
                    principalTable: "devices",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "fk_device_links_devices_to_id",
                    column: x => x.to_id,
                    principalSchema: "wp_registry",
                    principalTable: "devices",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_device_links_kind_from_id_to_id",
            schema: "wp_registry",
            table: "device_links",
            columns: new[] { "kind", "from_id", "to_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_device_links_to_id",
            schema: "wp_registry",
            table: "device_links",
            column: "to_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "device_links", schema: "wp_registry");

        migrationBuilder.CreateTable(
            name: "device_links",
            schema: "wp_registry",
            columns: table => new
            {
                source_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "text", nullable: false),
                target_device_id = table.Column<Guid>(type: "uuid", nullable: false),
                metadata = table.Column<string>(type: "jsonb", nullable: false),
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
            name: "ix_device_links_target_device_id",
            schema: "wp_registry",
            table: "device_links",
            column: "target_device_id");

        migrationBuilder.DropColumn(name: "comment", schema: "wp_registry", table: "outbox");
        migrationBuilder.DropColumn(name: "change_set", schema: "wp_registry", table: "outbox");
        migrationBuilder.DropColumn(name: "origin", schema: "wp_registry", table: "outbox");
        migrationBuilder.DropColumn(name: "actor", schema: "wp_registry", table: "outbox");

        migrationBuilder.AddColumn<string>(
            name: "actor",
            schema: "wp_registry",
            table: "outbox",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "reason",
            schema: "wp_registry",
            table: "outbox",
            type: "text",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "correlation_id",
            schema: "wp_registry",
            table: "outbox",
            type: "text",
            nullable: true);
    }
}

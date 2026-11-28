#pragma warning disable IDE0005
#pragma warning disable CA1861

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchpoint.Rights.Internal.Storage.Migrations;

/// <inheritdoc />
internal sealed partial class InitialRights : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "wp_rights");

        migrationBuilder.CreateTable(
            name: "grants",
            schema: "wp_rights",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                scope = table.Column<string>(type: "jsonb", nullable: false),
                schedule_calendar = table.Column<string>(type: "text", nullable: true),
                constraints = table.Column<string>(type: "jsonb", nullable: false),
                source = table.Column<string>(type: "jsonb", nullable: false),
                identity_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_grants", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "user_groups",
            schema: "wp_rights",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "jsonb", nullable: false),
                description = table.Column<string>(type: "text", nullable: true),
                managers = table.Column<string>(type: "jsonb", nullable: false),
                version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_groups", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "user_group_members",
            schema: "wp_rights",
            columns: table => new
            {
                group_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_group_members", x => new { x.group_id, x.user_id });
            });

        migrationBuilder.CreateIndex(
            name: "ix_grants_user_id",
            schema: "wp_rights",
            table: "grants",
            column: "user_id");

        migrationBuilder.CreateIndex(
            name: "ix_grants_user_id_identity_hash",
            schema: "wp_rights",
            table: "grants",
            columns: new[] { "user_id", "identity_hash" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "user_group_members", schema: "wp_rights");
        migrationBuilder.DropTable(name: "user_groups", schema: "wp_rights");
        migrationBuilder.DropTable(name: "grants", schema: "wp_rights");
    }
}

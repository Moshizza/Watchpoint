#pragma warning disable IDE0005
#pragma warning disable CA1861

using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchpoint.Registry.Internal.Storage.Migrations;

/// <inheritdoc />
internal sealed partial class AddDiscoveredItems : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "discovered_items",
            schema: "wp_registry",
            columns: table => new
            {
                connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                native_id = table.Column<string>(type: "text", nullable: false),
                kind = table.Column<string>(type: "text", nullable: false),
                server_native_id = table.Column<string>(type: "text", nullable: false),
                parent_native_id = table.Column<string>(type: "text", nullable: true),
                type_id = table.Column<string>(type: "text", nullable: false),
                type_version = table.Column<string>(type: "text", nullable: false),
                vendor_name = table.Column<string>(type: "text", nullable: false),
                vendor_attributes = table.Column<Dictionary<string, string>>(type: "jsonb", nullable: false),
                status = table.Column<string>(type: "text", nullable: false),
                first_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_seen = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_discovered_items", x => new { x.connection_id, x.native_id });
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "discovered_items",
            schema: "wp_registry");
    }
}

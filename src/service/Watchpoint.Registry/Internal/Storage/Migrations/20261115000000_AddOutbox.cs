#pragma warning disable IDE0005
#pragma warning disable CA1861

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Watchpoint.Registry.Internal.Storage.Migrations;

/// <inheritdoc />
internal sealed partial class AddOutbox : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "revision",
            schema: "wp_registry",
            columns: table => new
            {
                id = table.Column<int>(type: "integer", nullable: false),
                value = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_revision", x => x.id);
            });

        // Column types stated: the target model of the migration is empty (project convention), so EF cannot infer them.
        migrationBuilder.InsertData(
            schema: "wp_registry",
            table: "revision",
            columns: new[] { "id", "value" },
            columnTypes: new[] { "integer", "bigint" },
            values: new object[] { 1, 0L });

        migrationBuilder.CreateTable(
            name: "outbox",
            schema: "wp_registry",
            columns: table => new
            {
                revision = table.Column<long>(type: "bigint", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                actor = table.Column<string>(type: "text", nullable: true),
                reason = table.Column<string>(type: "text", nullable: true),
                correlation_id = table.Column<string>(type: "text", nullable: true),
                changes = table.Column<string>(type: "jsonb", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_outbox", x => x.revision);
            });

        migrationBuilder.CreateIndex(
            name: "ix_outbox_sent_at",
            schema: "wp_registry",
            table: "outbox",
            column: "sent_at");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "outbox",
            schema: "wp_registry");

        migrationBuilder.DropTable(
            name: "revision",
            schema: "wp_registry");
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class SwitchboardNodeSetup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Disabled",
                table: "switchboard_nodes",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Ext",
                table: "switchboard_nodes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDefaultTime",
                table: "switchboard_nodes",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Json",
                table: "switchboard_nodes",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "JsonHash",
                table: "switchboard_nodes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MissingAudio",
                table: "switchboard_nodes",
                type: "boolean",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "switchboard_node_snapshots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    NodeId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    JsonHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Json = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_switchboard_node_snapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_switchboard_node_snapshots_SiteId_NodeId_CapturedAt",
                table: "switchboard_node_snapshots",
                columns: new[] { "SiteId", "NodeId", "CapturedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "switchboard_node_snapshots");

            migrationBuilder.DropColumn(
                name: "Disabled",
                table: "switchboard_nodes");

            migrationBuilder.DropColumn(
                name: "Ext",
                table: "switchboard_nodes");

            migrationBuilder.DropColumn(
                name: "IsDefaultTime",
                table: "switchboard_nodes");

            migrationBuilder.DropColumn(
                name: "Json",
                table: "switchboard_nodes");

            migrationBuilder.DropColumn(
                name: "JsonHash",
                table: "switchboard_nodes");

            migrationBuilder.DropColumn(
                name: "MissingAudio",
                table: "switchboard_nodes");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class GroupMappings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_grants_SiteId_UserId_Kind_Key",
                table: "grants");

            migrationBuilder.AddColumn<bool>(
                name: "ByGroups",
                table: "grants",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Groups",
                table: "grants",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "group_mappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Group = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_mappings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "group_mapping_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MappingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AllowRecordings = table.Column<bool>(type: "boolean", nullable: false),
                    AllowVoicemail = table.Column<bool>(type: "boolean", nullable: false),
                    AllowTranscripts = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_group_mapping_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_group_mapping_lines_group_mappings_MappingId",
                        column: x => x.MappingId,
                        principalTable: "group_mappings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_grants_SiteId_UserId_Kind_Key_ByGroups",
                table: "grants",
                columns: new[] { "SiteId", "UserId", "Kind", "Key", "ByGroups" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_mapping_lines_MappingId_Kind_Key",
                table: "group_mapping_lines",
                columns: new[] { "MappingId", "Kind", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_group_mappings_SiteId_Group",
                table: "group_mappings",
                columns: new[] { "SiteId", "Group" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "group_mapping_lines");

            migrationBuilder.DropTable(
                name: "group_mappings");

            migrationBuilder.DropIndex(
                name: "IX_grants_SiteId_UserId_Kind_Key_ByGroups",
                table: "grants");

            migrationBuilder.DropColumn(
                name: "ByGroups",
                table: "grants");

            migrationBuilder.DropColumn(
                name: "Groups",
                table: "grants");

            migrationBuilder.CreateIndex(
                name: "IX_grants_SiteId_UserId_Kind_Key",
                table: "grants",
                columns: new[] { "SiteId", "UserId", "Kind", "Key" },
                unique: true);
        }
    }
}

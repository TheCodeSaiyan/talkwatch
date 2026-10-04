using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "console_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UnifiOs = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    UnifiOsChannel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Talk = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TalkChannel = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_console_versions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_console_versions_SiteId_SeenAt",
                table: "console_versions",
                columns: new[] { "SiteId", "SeenAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "console_versions");
        }
    }
}

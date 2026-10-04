using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class Retention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "retention_settings",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Preset = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    CallDays = table.Column<int>(type: "integer", nullable: true),
                    AudioDays = table.Column<int>(type: "integer", nullable: true),
                    MinimumDays = table.Column<int>(type: "integer", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSweepAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSweepCalls = table.Column<int>(type: "integer", nullable: false),
                    LastSweepFiles = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_settings", x => x.SiteId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "retention_settings");
        }
    }
}

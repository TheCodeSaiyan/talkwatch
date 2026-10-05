using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class ConsoleSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "console_settings",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsoleUrl = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Username = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    ProtectedPassword = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CertificateSha256 = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Route = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ProtectedWireGuardConfig = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ProtectedTailscaleAuthKey = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    TailscaleTags = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_console_settings", x => x.SiteId);
                    table.ForeignKey(
                        name: "FK_console_settings_sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "console_settings");
        }
    }
}

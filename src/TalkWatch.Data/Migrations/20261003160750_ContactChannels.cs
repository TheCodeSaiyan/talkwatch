using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class ContactChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContactUuid",
                table: "alert_channels",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_channels_SiteId_ContactUuid",
                table: "alert_channels",
                columns: new[] { "SiteId", "ContactUuid" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_alert_channels_SiteId_ContactUuid",
                table: "alert_channels");

            migrationBuilder.DropColumn(
                name: "ContactUuid",
                table: "alert_channels");
        }
    }
}

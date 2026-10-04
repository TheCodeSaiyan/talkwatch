using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AckConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_EventId_FlowId_ChannelId_Stage",
                table: "alert_deliveries");

            migrationBuilder.AddColumn<bool>(
                name: "Acknowledgement",
                table: "alert_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_EventId_FlowId_ChannelId_Stage_Acknowledge~",
                table: "alert_deliveries",
                columns: new[] { "EventId", "FlowId", "ChannelId", "Stage", "Acknowledgement" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_EventId_FlowId_ChannelId_Stage_Acknowledge~",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "Acknowledgement",
                table: "alert_deliveries");

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_EventId_FlowId_ChannelId_Stage",
                table: "alert_deliveries",
                columns: new[] { "EventId", "FlowId", "ChannelId", "Stage" },
                unique: true);
        }
    }
}

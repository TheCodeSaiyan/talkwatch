using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_alert_rule_channels",
                table: "alert_rule_channels");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_EventId_RuleId_ChannelId",
                table: "alert_deliveries");

            migrationBuilder.AddColumn<int>(
                name: "EscalateAfterMinutes",
                table: "alert_rules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Escalation",
                table: "alert_rule_channels",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AcknowledgedAt",
                table: "alert_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcknowledgedBy",
                table: "alert_events",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SnoozedUntil",
                table: "alert_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Escalation",
                table: "alert_deliveries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "QuietDays",
                table: "alert_channels",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietEnd",
                table: "alert_channels",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "QuietStart",
                table: "alert_channels",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddPrimaryKey(
                name: "PK_alert_rule_channels",
                table: "alert_rule_channels",
                columns: new[] { "RuleId", "ChannelId", "Escalation" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_EventId_RuleId_ChannelId_Escalation",
                table: "alert_deliveries",
                columns: new[] { "EventId", "RuleId", "ChannelId", "Escalation" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_alert_rule_channels",
                table: "alert_rule_channels");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_EventId_RuleId_ChannelId_Escalation",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "EscalateAfterMinutes",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "Escalation",
                table: "alert_rule_channels");

            migrationBuilder.DropColumn(
                name: "AcknowledgedAt",
                table: "alert_events");

            migrationBuilder.DropColumn(
                name: "AcknowledgedBy",
                table: "alert_events");

            migrationBuilder.DropColumn(
                name: "SnoozedUntil",
                table: "alert_events");

            migrationBuilder.DropColumn(
                name: "Escalation",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "QuietDays",
                table: "alert_channels");

            migrationBuilder.DropColumn(
                name: "QuietEnd",
                table: "alert_channels");

            migrationBuilder.DropColumn(
                name: "QuietStart",
                table: "alert_channels");

            migrationBuilder.AddPrimaryKey(
                name: "PK_alert_rule_channels",
                table: "alert_rule_channels",
                columns: new[] { "RuleId", "ChannelId" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_EventId_RuleId_ChannelId",
                table: "alert_deliveries",
                columns: new[] { "EventId", "RuleId", "ChannelId" },
                unique: true);
        }
    }
}

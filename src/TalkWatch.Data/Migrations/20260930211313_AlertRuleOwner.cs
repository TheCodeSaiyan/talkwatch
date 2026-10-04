using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertRuleOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                table: "alert_rules",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_OwnerUserId",
                table: "alert_rules",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_alert_rules_AspNetUsers_OwnerUserId",
                table: "alert_rules",
                column: "OwnerUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_alert_rules_AspNetUsers_OwnerUserId",
                table: "alert_rules");

            migrationBuilder.DropIndex(
                name: "IX_alert_rules_OwnerUserId",
                table: "alert_rules");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                table: "alert_rules");
        }
    }
}

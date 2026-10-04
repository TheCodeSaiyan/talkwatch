using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class CallBacks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReturnedAt",
                table: "calls",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnedBy",
                table: "calls",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnedHow",
                table: "calls",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_calls_SiteId_FromE164",
                table: "calls",
                columns: new[] { "SiteId", "FromE164" });

            migrationBuilder.CreateIndex(
                name: "IX_calls_SiteId_ToE164",
                table: "calls",
                columns: new[] { "SiteId", "ToE164" });

            // Calls stored before this: every missed call a later call returned is marked now, so the list starts true.
            migrationBuilder.Sql(TalkWatch.Data.CallBacks.MarkReturned + ";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_calls_SiteId_FromE164",
                table: "calls");

            migrationBuilder.DropIndex(
                name: "IX_calls_SiteId_ToE164",
                table: "calls");

            migrationBuilder.DropColumn(
                name: "ReturnedAt",
                table: "calls");

            migrationBuilder.DropColumn(
                name: "ReturnedBy",
                table: "calls");

            migrationBuilder.DropColumn(
                name: "ReturnedHow",
                table: "calls");
        }
    }
}

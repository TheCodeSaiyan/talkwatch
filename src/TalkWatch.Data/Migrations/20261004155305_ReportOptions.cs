using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportOptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ComparePrevious",
                table: "reports",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "HoursDays",
                table: "reports",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoursFrom",
                table: "reports",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<TimeOnly>(
                name: "HoursTo",
                table: "reports",
                type: "time without time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SplitByNumber",
                table: "reports",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComparePrevious",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "HoursDays",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "HoursFrom",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "HoursTo",
                table: "reports");

            migrationBuilder.DropColumn(
                name: "SplitByNumber",
                table: "reports");
        }
    }
}

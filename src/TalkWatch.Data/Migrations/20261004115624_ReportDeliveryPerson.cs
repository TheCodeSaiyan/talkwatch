using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReportDeliveryPerson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "UserId",
                table: "report_deliveries",
                type: "uuid",
                nullable: true);

            // Copies already made for a person: the one their copy was built for, named as its recipient. A channel's copy
            // carries the channel's name, so it is left without one.
            migrationBuilder.Sql("""
                UPDATE report_deliveries AS d SET "UserId" = r."AudienceUserId"
                FROM report_runs AS r, "AspNetUsers" AS u
                WHERE d."RunId" = r."Id" AND u."Id" = r."AudienceUserId" AND u."UserName" = d."Recipient";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UserId",
                table: "report_deliveries");
        }
    }
}

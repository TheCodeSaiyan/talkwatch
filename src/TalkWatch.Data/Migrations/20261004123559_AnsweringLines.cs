using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AnsweringLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "answering_lines",
                columns: table => new
                {
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Phrases = table.Column<string>(type: "text", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_answering_lines", x => new { x.SiteId, x.ContactId });
                    table.ForeignKey(
                        name: "FK_answering_lines_sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "call_findings",
                columns: table => new
                {
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Finding = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ContactId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Phrase = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ByHand = table.Column<bool>(type: "boolean", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_findings", x => x.CallId);
                    table.ForeignKey(
                        name: "FK_call_findings_calls_CallId",
                        column: x => x.CallId,
                        principalTable: "calls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "answering_lines");

            migrationBuilder.DropTable(
                name: "call_findings");
        }
    }
}

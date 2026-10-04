using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class Transcripts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "call_transcripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    TalkId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Summary = table.Column<string>(type: "text", nullable: true),
                    Sentiment = table.Column<double>(type: "double precision", nullable: true),
                    SentimentClass = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Lines = table.Column<string>(type: "jsonb", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    CopiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_transcripts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_call_transcripts_calls_CallId",
                        column: x => x.CallId,
                        principalTable: "calls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_call_transcripts_CallId",
                table: "call_transcripts",
                column: "CallId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "call_transcripts");
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "raw_payloads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    FetchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Body = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_raw_payloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sites",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DefaultRegion = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sites", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "calls",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    TalkUuid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Direction = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DurationSeconds = table.Column<int>(type: "integer", nullable: false),
                    FromRaw = table.Column<string>(type: "text", nullable: true),
                    FromE164 = table.Column<string>(type: "text", nullable: true),
                    ToRaw = table.Column<string>(type: "text", nullable: true),
                    ToE164 = table.Column<string>(type: "text", nullable: true),
                    AnsweredByRaw = table.Column<string>(type: "text", nullable: true),
                    AnsweredByE164 = table.Column<string>(type: "text", nullable: true),
                    CallerName = table.Column<string>(type: "text", nullable: true),
                    HasRecording = table.Column<bool>(type: "boolean", nullable: false),
                    RecordingFilename = table.Column<string>(type: "text", nullable: true),
                    Country = table.Column<string>(type: "text", nullable: true),
                    QualityScore = table.Column<int>(type: "integer", nullable: true),
                    IngestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_calls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_calls_sites_SiteId",
                        column: x => x.SiteId,
                        principalTable: "sites",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "call_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Event = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EventUuid = table.Column<string>(type: "text", nullable: true),
                    DataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_call_events_calls_CallId",
                        column: x => x.CallId,
                        principalTable: "calls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "call_lines",
                columns: table => new
                {
                    CallId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_lines", x => new { x.CallId, x.Kind, x.Key });
                    table.ForeignKey(
                        name: "FK_call_lines_calls_CallId",
                        column: x => x.CallId,
                        principalTable: "calls",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_call_events_CallId_Sequence",
                table: "call_events",
                columns: new[] { "CallId", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_call_lines_SiteId_Kind_Key",
                table: "call_lines",
                columns: new[] { "SiteId", "Kind", "Key" });

            migrationBuilder.CreateIndex(
                name: "IX_calls_SiteId_TalkUuid",
                table: "calls",
                columns: new[] { "SiteId", "TalkUuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_calls_SiteId_Time",
                table: "calls",
                columns: new[] { "SiteId", "Time" });

            migrationBuilder.CreateIndex(
                name: "IX_raw_payloads_SiteId_Endpoint_FetchedAt",
                table: "raw_payloads",
                columns: new[] { "SiteId", "Endpoint", "FetchedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "call_events");

            migrationBuilder.DropTable(
                name: "call_lines");

            migrationBuilder.DropTable(
                name: "raw_payloads");

            migrationBuilder.DropTable(
                name: "calls");

            migrationBuilder.DropTable(
                name: "sites");
        }
    }
}

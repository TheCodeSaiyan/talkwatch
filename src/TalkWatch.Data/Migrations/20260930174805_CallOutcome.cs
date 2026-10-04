using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class CallOutcome : Migration
    {
        /// <summary>
        /// CallOutcomes.Of in SQL, for the calls stored before outcomes were. Public so a test can hold the two
        /// together over every fixture call; kept here, as it was when this migration ran.
        /// </summary>
        public const string Backfill = """
            UPDATE calls c SET "Outcome" = CASE
                WHEN lower(c."Direction") = 'out' THEN 'Outbound'
                WHEN lower(c."Direction") <> 'in' THEN 'Other'
                WHEN lower(c."Status") = 'blocked' THEN 'Blocked'
                WHEN EXISTS (SELECT 1 FROM call_events e WHERE e."CallId" = c."Id" AND e."Event" = 'vm_msg_recorded') THEN 'Voicemail'
                WHEN lower(c."Status") IN ('cancelled', 'refused', 'timed_out') THEN 'Missed'
                WHEN EXISTS (SELECT 1 FROM call_events e WHERE e."CallId" = c."Id" AND e."Event" = 'call_accepted') THEN 'Answered'
                WHEN NOT EXISTS (SELECT 1 FROM call_events e WHERE e."CallId" = c."Id")
                    THEN CASE WHEN lower(c."Status") = 'accepted' THEN 'Answered' ELSE 'Unknown' END
                WHEN NOT EXISTS (SELECT 1 FROM call_events e WHERE e."CallId" = c."Id" AND e."Event" = 'call_hangup') THEN 'InProgress'
                WHEN EXISTS (SELECT 1 FROM call_events e WHERE e."CallId" = c."Id"
                        AND e."Event" IN ('seq_call_trying_endpoints', 'call_sent_to_voicemail')) THEN 'Missed'
                ELSE 'HungUpAtSwitchboard'
            END;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "calls",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.Sql(Backfill);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "calls");
        }
    }
}

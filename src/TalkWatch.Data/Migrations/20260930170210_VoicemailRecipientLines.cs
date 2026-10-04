using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class VoicemailRecipientLines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Calls stored before voicemail recipients counted as lines: give each call sent to someone's voicemail
            // that person's line, from the events already stored. New calls get it when they are stored.
            migrationBuilder.Sql("""
                INSERT INTO call_lines ("CallId", "Kind", "Key", "SiteId")
                SELECT DISTINCT e."CallId", 'User', recipient, e."SiteId"
                  FROM call_events e,
                       jsonb_array_elements_text(e."DataJson" -> 'recipient_user_uuids') AS recipient
                 WHERE e."Event" IN ('call_sent_to_voicemail', 'vm_msg_recorded', 'vm_recording_canceled')
                   AND jsonb_typeof(e."DataJson" -> 'recipient_user_uuids') = 'array'
                ON CONFLICT DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the lines added cannot be told apart from ones the ingestor adds, and are correct either way.
        }
    }
}

using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TalkWatch.Data.Migrations
{
    /// <inheritdoc />
    public partial class AlertFlows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "alert_flows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Trigger = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Definition = table.Column<string>(type: "jsonb", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_flows", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_flows_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_flow_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlowId = table.Column<Guid>(type: "uuid", nullable: false),
                    Plan = table.Column<string>(type: "jsonb", nullable: false),
                    NextStage = table.Column<int>(type: "integer", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_flow_runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_flow_runs_alert_events_EventId",
                        column: x => x.EventId,
                        principalTable: "alert_events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_flow_runs_alert_flows_FlowId",
                        column: x => x.FlowId,
                        principalTable: "alert_flows",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Before anything about rules is dropped: every rule becomes a flow with the same id, so the deliveries it
            // sent stay linked, and an escalation already counting down carries on as a run.
            migrationBuilder.Sql(RulesToFlows);
            migrationBuilder.Sql(EscalationsToRuns);

            migrationBuilder.DropForeignKey(
                name: "FK_alert_deliveries_alert_rules_RuleId",
                table: "alert_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_EventId_RuleId_ChannelId_Escalation",
                table: "alert_deliveries");

            migrationBuilder.RenameColumn(
                name: "RuleId",
                table: "alert_deliveries",
                newName: "FlowId");

            // An escalation was sent at high priority, and was a rule's second stage.
            migrationBuilder.RenameColumn(
                name: "Escalation",
                table: "alert_deliveries",
                newName: "Urgent");

            migrationBuilder.RenameIndex(
                name: "IX_alert_deliveries_RuleId",
                table: "alert_deliveries",
                newName: "IX_alert_deliveries_FlowId");

            migrationBuilder.AddColumn<int>(
                name: "Stage",
                table: "alert_deliveries",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""UPDATE alert_deliveries SET "Stage" = 1 WHERE "Urgent";""");

            migrationBuilder.DropTable(
                name: "alert_rule_channels");

            migrationBuilder.DropTable(
                name: "alert_rules");

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_EventId_FlowId_ChannelId_Stage",
                table: "alert_deliveries",
                columns: new[] { "EventId", "FlowId", "ChannelId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_flow_runs_EventId_FlowId",
                table: "alert_flow_runs",
                columns: new[] { "EventId", "FlowId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_flow_runs_FlowId",
                table: "alert_flow_runs",
                column: "FlowId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_flow_runs_State_DueAt",
                table: "alert_flow_runs",
                columns: new[] { "State", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_alert_flows_OwnerUserId",
                table: "alert_flows",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_flows_SiteId_Trigger",
                table: "alert_flows",
                columns: new[] { "SiteId", "Trigger" });

            migrationBuilder.AddForeignKey(
                name: "FK_alert_deliveries_alert_flows_FlowId",
                table: "alert_deliveries",
                column: "FlowId",
                principalTable: "alert_flows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <summary>
        /// A rule as a flow: its line, window and caller condition become conditions; it notifies its channels, then,
        /// with escalation, waits and notifies its escalation channels as urgent. A rule left with no channels (they were
        /// removed) becomes a flow that notifies nobody, as the rule did.
        /// </summary>
        public const string RulesToFlows = """
            INSERT INTO alert_flows ("Id", "SiteId", "Name", "Trigger", "Definition", "Enabled", "CreatedAt", "UpdatedAt", "OwnerUserId")
            SELECT r."Id", r."SiteId", r."Name", r."EventType",
                jsonb_build_object(
                    'trigger', r."EventType",
                    'conditions',
                        (CASE WHEN r."LineKind" IS NOT NULL
                            THEN jsonb_build_array(jsonb_build_object('kind', 'line', 'lines', jsonb_build_array(jsonb_build_object('kind', r."LineKind", 'key', r."LineKey"))))
                            ELSE '[]'::jsonb END)
                        || (CASE WHEN r."WindowDays" IS NOT NULL
                            THEN jsonb_build_array(jsonb_build_object('kind', 'time', 'days', r."WindowDays", 'start', r."WindowStart"::text, 'end', r."WindowEnd"::text, 'outside', r."WindowOutside"))
                            ELSE '[]'::jsonb END)
                        || (CASE WHEN r."CallerMode" <> 'Any'
                            THEN jsonb_build_array(jsonb_build_object('kind', 'caller', 'mode', r."CallerMode", 'entries', to_jsonb(string_to_array(coalesce(r."CallerNumbers", ''), ','))))
                            ELSE '[]'::jsonb END),
                    'steps',
                        (SELECT CASE WHEN count(*) > 0
                            THEN jsonb_build_array(jsonb_build_object('kind', 'notify', 'to', jsonb_agg(jsonb_build_object('channel', c."ChannelId"))))
                            ELSE '[]'::jsonb END
                         FROM alert_rule_channels c WHERE c."RuleId" = r."Id" AND NOT c."Escalation")
                        || (SELECT CASE WHEN r."EscalateAfterMinutes" IS NOT NULL AND count(*) > 0
                            THEN jsonb_build_array(
                                jsonb_build_object('kind', 'wait', 'minutes', r."EscalateAfterMinutes"),
                                jsonb_build_object('kind', 'notify', 'to', jsonb_agg(jsonb_build_object('channel', c."ChannelId")), 'urgent', true))
                            ELSE '[]'::jsonb END
                         FROM alert_rule_channels c WHERE c."RuleId" = r."Id" AND c."Escalation")),
                r."Enabled", r."CreatedAt", r."CreatedAt", r."OwnerUserId"
            FROM alert_rules r;
            """;

        /// <summary>
        /// An alert from the last day that a rule sent, nobody has acknowledged and has not yet escalated: a run waiting
        /// for its second stage, due when the escalation would have been.
        /// </summary>
        public const string EscalationsToRuns = """
            INSERT INTO alert_flow_runs ("Id", "SiteId", "EventId", "FlowId", "Plan", "NextStage", "DueAt", "State", "CreatedAt")
            SELECT gen_random_uuid(), e."SiteId", e."Id", r."Id",
                jsonb_build_array(
                    jsonb_build_object('waitMinutes', 0, 'notify', jsonb_build_array(jsonb_build_object('to',
                        (SELECT coalesce(jsonb_agg(jsonb_build_object('channel', c."ChannelId")), '[]'::jsonb) FROM alert_rule_channels c WHERE c."RuleId" = r."Id" AND NOT c."Escalation")))),
                    jsonb_build_object('waitMinutes', r."EscalateAfterMinutes", 'notify', jsonb_build_array(jsonb_build_object('urgent', true, 'to',
                        (SELECT jsonb_agg(jsonb_build_object('channel', c."ChannelId")) FROM alert_rule_channels c WHERE c."RuleId" = r."Id" AND c."Escalation"))))),
                1, e."At" + make_interval(mins => r."EscalateAfterMinutes"), 'Running', now()
            FROM alert_rules r
            JOIN alert_events e ON e."SiteId" = r."SiteId"
            WHERE r."EscalateAfterMinutes" IS NOT NULL
              AND EXISTS (SELECT 1 FROM alert_rule_channels c WHERE c."RuleId" = r."Id" AND c."Escalation")
              AND e."AcknowledgedAt" IS NULL
              AND e."At" > now() - interval '1 day'
              AND EXISTS (SELECT 1 FROM alert_deliveries d WHERE d."EventId" = e."Id" AND d."RuleId" = r."Id" AND NOT d."Escalation")
              AND NOT EXISTS (SELECT 1 FROM alert_deliveries d WHERE d."EventId" = e."Id" AND d."RuleId" = r."Id" AND d."Escalation");
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_alert_deliveries_alert_flows_FlowId",
                table: "alert_deliveries");

            migrationBuilder.DropTable(
                name: "alert_flow_runs");

            migrationBuilder.DropTable(
                name: "alert_flows");

            migrationBuilder.DropIndex(
                name: "IX_alert_deliveries_EventId_FlowId_ChannelId_Stage",
                table: "alert_deliveries");

            migrationBuilder.DropColumn(
                name: "Stage",
                table: "alert_deliveries");

            migrationBuilder.RenameColumn(
                name: "Urgent",
                table: "alert_deliveries",
                newName: "Escalation");

            migrationBuilder.RenameColumn(
                name: "FlowId",
                table: "alert_deliveries",
                newName: "RuleId");

            migrationBuilder.RenameIndex(
                name: "IX_alert_deliveries_FlowId",
                table: "alert_deliveries",
                newName: "IX_alert_deliveries_RuleId");

            migrationBuilder.CreateTable(
                name: "alert_rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CallerMode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CallerNumbers = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    EscalateAfterMinutes = table.Column<int>(type: "integer", nullable: true),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LineKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LineKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    WindowDays = table.Column<int>(type: "integer", nullable: true),
                    WindowEnd = table.Column<TimeOnly>(type: "time without time zone", nullable: true),
                    WindowOutside = table.Column<bool>(type: "boolean", nullable: false),
                    WindowStart = table.Column<TimeOnly>(type: "time without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_alert_rules_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "alert_rule_channels",
                columns: table => new
                {
                    RuleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    Escalation = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_alert_rule_channels", x => new { x.RuleId, x.ChannelId, x.Escalation });
                    table.ForeignKey(
                        name: "FK_alert_rule_channels_alert_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalTable: "alert_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_alert_rule_channels_alert_rules_RuleId",
                        column: x => x.RuleId,
                        principalTable: "alert_rules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_alert_deliveries_EventId_RuleId_ChannelId_Escalation",
                table: "alert_deliveries",
                columns: new[] { "EventId", "RuleId", "ChannelId", "Escalation" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_alert_rule_channels_ChannelId",
                table: "alert_rule_channels",
                column: "ChannelId");

            migrationBuilder.CreateIndex(
                name: "IX_alert_rules_OwnerUserId",
                table: "alert_rules",
                column: "OwnerUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_alert_deliveries_alert_rules_RuleId",
                table: "alert_deliveries",
                column: "RuleId",
                principalTable: "alert_rules",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}

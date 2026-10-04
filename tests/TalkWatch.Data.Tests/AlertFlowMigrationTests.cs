using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data.Tests;

public sealed class AlertFlowMigrationTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Rules_become_flows_that_do_what_they_did_and_a_pending_escalation_carries_on()
    {
        var connection = postgres.EmptyDatabase();
        await using (var before = PostgresFixture.Context(connection, AccessScope.Nobody))
        {
            await before.Database.MigrateAsync("20260930211313_AlertRuleOwner", Ct);
        }

        var siteId = Guid.NewGuid();
        await using var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);

        // Written in SQL for the schema as it was then: rules and their channels are gone from the model today.
        Guid team = Guid.NewGuid(), boss = Guid.NewGuid(), escalating = Guid.NewGuid(), plain = Guid.NewGuid(), pending = Guid.NewGuid(), done = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO alert_channels ("Id", "SiteId", "Name", "Kind", "Target", "Enabled", "CreatedAt")
            VALUES ({team}, {siteId}, 'Team', 'Ntfy', 'https://ntfy.test/team', true, now()), ({boss}, {siteId}, 'Boss', 'Email', 'boss@example.test', true, now());

            INSERT INTO alert_rules ("Id", "SiteId", "Name", "EventType", "LineKind", "LineKey", "WindowDays", "WindowStart", "WindowEnd", "WindowOutside",
                "CallerMode", "CallerNumbers", "EscalateAfterMinutes", "Enabled", "CreatedAt")
            VALUES ({escalating}, {siteId}, 'Missed on sales, out of hours', 'MissedCall', 'Did', '+441144960042', 62, '09:00', '17:30', true,
                    'Except', 'withheld,+44800*', 10, true, now()),
                   ({plain}, {siteId}, 'Every voicemail', 'Voicemail', NULL, NULL, NULL, NULL, NULL, false, 'Any', NULL, NULL, false, now());
            INSERT INTO alert_rule_channels ("RuleId", "ChannelId", "Escalation")
            VALUES ({escalating}, {team}, false), ({escalating}, {boss}, true), ({plain}, {team}, false);

            INSERT INTO alert_events ("Id", "SiteId", "Type", "Key", "At", "Title", "Message")
            VALUES ({pending}, {siteId}, 'MissedCall', 'call:a:MissedCall', now() - interval '3 minutes', 'Missed call', 'From someone'),
                   ({done}, {siteId}, 'MissedCall', 'call:b:MissedCall', now() - interval '30 minutes', 'Missed call', 'From someone');
            INSERT INTO alert_deliveries ("Id", "SiteId", "EventId", "RuleId", "ChannelId", "Escalation", "State", "Attempts", "NextAttemptAt", "CreatedAt")
            VALUES ({Guid.NewGuid()}, {siteId}, {pending}, {escalating}, {team}, false, 'Sent', 1, now(), now()),
                   ({Guid.NewGuid()}, {siteId}, {done}, {escalating}, {team}, false, 'Sent', 1, now(), now()),
                   ({Guid.NewGuid()}, {siteId}, {done}, {escalating}, {boss}, true, 'Sent', 1, now(), now());
            """, Ct);

        await db.Database.MigrateAsync(Ct);

        var flows = await db.AlertFlows.AsNoTracking().ToDictionaryAsync(f => f.Id, Ct);
        var outOfHours = Flows.Read(flows[escalating].Definition)!;
        Assert.Null(Flows.Problem(outOfHours));
        Assert.Equal(AlertEventType.MissedCall, outOfHours.Trigger);
        Assert.Equivalent(new FlowCondition[]
        {
            new LineCondition([new LineRef(LineKind.Did, "+441144960042")]),
            new TimeCondition(62, new TimeOnly(9, 0), new TimeOnly(17, 30), Outside: true),
            new CallerFlowCondition(CallerMode.Except, [CallerCondition.Withheld, "+44800*"]),
        }, outOfHours.Conditions);
        Assert.Equivalent(new FlowStep[]
        {
            new NotifyStep([FlowRecipient.ToChannel(team)]),
            new WaitStep(10),
            new NotifyStep([FlowRecipient.ToChannel(boss)], Urgent: true),
        }, outOfHours.Steps);

        var voicemail = Flows.Read(flows[plain].Definition)!;
        Assert.False(flows[plain].Enabled);
        Assert.Empty(voicemail.Conditions);
        Assert.Equivalent(new FlowStep[] { new NotifyStep([FlowRecipient.ToChannel(team)]) }, voicemail.Steps);

        // Only the alert still waiting to escalate carries on, at the second stage, due when it would have escalated.
        var run = await db.AlertFlowRuns.AsNoTracking().SingleAsync(Ct);
        var at = await db.AlertEvents.Where(e => e.Id == pending).Select(e => e.At).SingleAsync(Ct);
        Assert.Equal((pending, escalating, 1, FlowRunState.Running), (run.EventId, run.FlowId, run.NextStage, run.State));
        Assert.Equal(at.AddMinutes(10), run.DueAt);
        Assert.Equivalent(new[] { new FlowStage(0, [new NotifyStep([FlowRecipient.ToChannel(team)])]), new FlowStage(10, [new NotifyStep([FlowRecipient.ToChannel(boss)], Urgent: true)]) },
            Flows.ReadPlan(run.Plan));

        // Deliveries keep their history: the flow is the rule they came from, and an escalation was its second stage.
        var deliveries = await db.AlertDeliveries.AsNoTracking().Where(d => d.EventId == done).OrderBy(d => d.Stage).ToListAsync(Ct);
        Assert.Equal([(escalating, 0, false), (escalating, 1, true)], deliveries.Select(d => (d.FlowId, d.Stage, d.Urgent)));
    }
}

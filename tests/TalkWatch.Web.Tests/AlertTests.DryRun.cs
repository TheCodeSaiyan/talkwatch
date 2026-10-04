using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>A flow tried on past calls, saved or not: what it would have done, judged as live, with nothing sent.</summary>
public sealed partial class AlertTests
{
    private static async Task<AlertService.DryRun> TryAsync(Harness h, FlowDefinition flow)
    {
        using var scope = h.App.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await h.App.Services.GetRequiredService<AlertService>().DryRunAsync(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), flow, DateTimeOffset.MinValue, Ct);
    }

    [Fact]
    public async Task Trying_a_flow_on_past_calls_finds_the_calls_it_would_have_run_on_and_sends_nothing()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var missed = await h.DbAsync(db => db.Calls.Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Missed).Select(c => new { c.TalkUuid, c.FromE164 }).ToListAsync(Ct));
        Assert.NotEmpty(missed);
        var channel = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        var notify = new NotifyStep([FlowRecipient.ToChannel(channel)]);
        var alertsBefore = await h.DbAsync(db => db.AlertEvents.CountAsync(Ct));

        var every = await TryAsync(h, new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [notify] });
        var caller = missed.First(m => m.FromE164 is not null).FromE164!;
        var one = await TryAsync(h, new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall, Conditions = [new CallerFlowCondition(CallerMode.Only, [caller])], Steps = [notify],
        });
        var site = await TryAsync(h, new FlowDefinition { Trigger = AlertEventType.Drift, Steps = [notify] });

        Assert.Null(every.Why);
        Assert.Equal(missed.Count, every.Considered);
        Assert.Equal(missed.Select(m => m.TalkUuid).Order(), every.Hits.Select(x => x.Call.TalkUuid).Order());
        Assert.Equal([channel], every.Hits[0].Plan.Single().Notify.SelectMany(n => n.To).Select(r => r.Channel!.Value));
        // Judged on the same facts as live: only that caller's missed calls, of the same ones considered.
        Assert.Equal(missed.Count, one.Considered);
        Assert.Equal(missed.Where(m => m.FromE164 == caller).Select(m => m.TalkUuid).Order(), one.Hits.Select(x => x.Call.TalkUuid).Order());
        Assert.NotNull(site.Why);
        // Nothing raised, nothing sent.
        Assert.Equal(alertsBefore, await h.DbAsync(db => db.AlertEvents.CountAsync(Ct)));
        Assert.Equal(0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(Ct)));
        Assert.Empty(h.Receiver.Requests);
    }

    // The figures a flow can test are built from the calls themselves: a missed call counts towards its own caller's and
    // its own number's tally, so the lowest bar matches exactly the missed calls with a caller, and those on a number.
    [Fact]
    public async Task The_figures_a_flow_tests_are_built_from_each_calls_own_caller_and_number()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var missed = await h.DbAsync(db => db.Calls.Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Missed)
            .Select(c => new { c.TalkUuid, Caller = c.FromE164 != null, OnNumber = c.Lines.Any(l => l.Kind == LineKind.Did) }).ToListAsync(Ct));
        var notify = new NotifyStep([FlowRecipient.ToChannel(await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk"))]);

        var byCaller = await TryAsync(h, new FlowDefinition { Trigger = AlertEventType.MissedCall, Conditions = [new CallerMissedCondition(1, CallerMissedCondition.MaxMinutes)], Steps = [notify] });
        var byNumber = await TryAsync(h, new FlowDefinition { Trigger = AlertEventType.MissedCall, Conditions = [new NumberMissedCondition(1, 60)], Steps = [notify] });

        Assert.Contains(missed, m => m.OnNumber);
        Assert.Equal(missed.Where(m => m.Caller).Select(m => m.TalkUuid).Order(), byCaller.Hits.Select(x => x.Call.TalkUuid).Order());
        Assert.Equal(missed.Where(m => m.OnNumber).Select(m => m.TalkUuid).Order(), byNumber.Hits.Select(x => x.Call.TalkUuid).Order());
    }
}

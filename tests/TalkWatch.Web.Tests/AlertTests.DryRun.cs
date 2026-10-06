using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Web.Components.Alerts;
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

    // The verdict is a figure in bold and the words after it: the space between them has to survive rendering, which drops
    // a text node that is only whitespace, as the space closing the bold once was.
    [Theory]
    [InlineData(1, 1, "It would have run on 1 of the 1 call in the last 7 days that started it.")]
    [InlineData(0, 3, "It would not have run of the 3 calls in the last 7 days that started it.")]
    [InlineData(2, 500, "It would have run on 2 of the 500 calls in the last 7 days that started it (the latest 500).")]
    public async Task What_a_tried_flow_would_have_done_reads_as_a_sentence(int hits, int considered, string expected)
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<DryRunVerdict>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(DryRunVerdict.Hits)] = hits, [nameof(DryRunVerdict.Considered)] = considered }))).ToHtmlString());

        Assert.Equal(expected, WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "")).Trim());
    }
}

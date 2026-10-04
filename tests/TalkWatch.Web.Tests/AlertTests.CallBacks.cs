using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    /// <summary>An outbound call to <paramref name="number"/> at <paramref name="at"/>, as the console would log it.</summary>
    private static string CallBack(string number, DateTimeOffset at) =>
        $$"""
        {"uuid": "call-back-{{at.ToUnixTimeSeconds()}}", "time": "{{at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}}",
         "direction": "out", "status": "accepted", "duration": 45, "from": "0002", "to": "{{number}}",
         "call_events": [{"time": "{{at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}}", "event": "call_started"}]}
        """;

    /// <summary>Stores history, then the missed call as if it had just happened, and sends what that raises.</summary>
    private static async Task<CallLogRecord> MissedJustNowAsync(Harness h)
    {
        var (missed, newer) = NewestMissed(h.Console);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();
        await h.DispatchAsync();
        return missed;
    }

    [Fact]
    public async Task Ringing_a_missed_caller_back_acknowledges_the_alert_and_stops_its_escalation()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        var manager = await h.AddChannelAsync("Manager", ChannelKind.Webhook, "https://hooks.test/manager");
        await h.AddFlowAsync("Missed, then the manager after an hour", new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(desk)]), new WaitStep(60), new NotifyStep([FlowRecipient.ToChannel(manager)], Urgent: true)],
        });
        var missed = await MissedJustNowAsync(h);
        Assert.Equal(["https://hooks.test/desk"], h.Receiver.Requests.Select(r => r.Uri.ToString()));

        // Twenty minutes later someone rings them back, from any phone, with the number as they'd dial it.
        h.Console.AddCall(CallBack(missed.From!, missed.Time + TimeSpan.FromMinutes(20)));
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(21);
        await h.PollAsync();
        await h.DispatchAsync();
        h.Clock.Now += TimeSpan.FromHours(2);
        await h.DispatchAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Type == AlertEventType.MissedCall, Ct));
        var call = await h.DbAsync(db => db.Calls.SingleAsync(c => c.TalkUuid == missed.Uuid, Ct));
        Assert.Equal(CallBackHow.CalledBack, call.ReturnedHow);
        Assert.Equal(AlertService.ByCallBack, alert.AcknowledgedBy);
        Assert.Equal(["https://hooks.test/desk", "https://hooks.test/desk"], h.Receiver.Requests.Select(r => r.Uri.ToString()));
        using var body = JsonDocument.Parse(h.Receiver.Requests[1].Body);
        Assert.Contains("Acknowledged by calling them back at", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    /// <summary>The call-back list, older calls included, decoded: Razor writes the + of a number as &amp;#x2B;.</summary>
    private static async Task<string> PageAsync(HttpClient browser) =>
        WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/callbacks?all=1", UriKind.Relative), Ct));

    [Fact]
    public async Task The_call_back_list_shows_missed_callers_only_to_those_who_may_see_them_and_marking_done_clears_them()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [desk]);
        var missed = await MissedJustNowAsync(h);
        var number = new NumberNormaliser("GB").ToE164(missed.From)!;
        await h.AddViewerAsync("elsewhere", "Did:+441174960999");
        using var viewer = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(viewer, "elsewhere", ViewerPassword);

        var listed = await PageAsync(h.Admin);
        var notTheirs = await PageAsync(viewer);
        var refused = await PostAsync(viewer, "/callbacks", "/callbacks/done", ("Number", number));

        Assert.Contains($"data-callback=\"{number}\"", listed, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-callback=\"{number}\"", notTheirs, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);

        var done = await PostAsync(h.Admin, "/callbacks", "/callbacks/done", ("Number", number));

        Assert.Contains("Marked%20done", done.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-callback=\"{number}\"", await PageAsync(h.Admin), StringComparison.Ordinal);
        var call = await h.DbAsync(db => db.Calls.SingleAsync(c => c.TalkUuid == missed.Uuid, Ct));
        Assert.Equal((CallBackHow.MarkedDone, TalkWatchApp.AdminUsername), (call.ReturnedHow!.Value, call.ReturnedBy));
        Assert.Equal(TalkWatchApp.AdminUsername, await h.DbAsync(db => db.AlertEvents.Where(e => e.CallId == call.Id && e.Type == AlertEventType.MissedCall).Select(e => e.AcknowledgedBy).SingleAsync(Ct)));
        Assert.Equal(1, await h.DbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "callback.done", Ct)));
        Assert.Contains("Marked done by admin", await PageAsync(h.Admin), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_dashboard_counts_missed_calls_got_back_to_and_the_time_it_took()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var missed = await MissedJustNowAsync(h);
        h.Console.AddCall(CallBack(missed.From!, missed.Time + TimeSpan.FromMinutes(20)));
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(21);
        await h.PollAsync();
        var day = TimeZoneInfo.ConvertTime(missed.Time, TimeZoneInfo.FindSystemTimeZoneById("Europe/London")).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var page = await h.Admin.GetStringAsync(new Uri($"/dashboard?from={day}&to={day}", UriKind.Relative), Ct);

        Assert.Matches("data-figure=\"called-back\"><strong>1 of \\d+</strong>", page);
        Assert.Contains("data-figure=\"call-back-time\"><strong>20 min</strong>", page, StringComparison.Ordinal);
    }
}

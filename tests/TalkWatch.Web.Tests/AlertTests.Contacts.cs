using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    // One of the captured contacts: Harper13 Hayden14, whose mobile this is.
    private const string ContactNumber = "+447700900880";
    private const string ContactName = "Harper13 Hayden14 (mobile)";

    private static string ContactCall(string uuid, DateTimeOffset at, string direction, bool answered)
    {
        var time = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var (from, to) = direction == "in" ? (ContactNumber, "+441144960042") : ("0002", ContactNumber);
        var events = direction == "out" ? "call_started call_hangup"
            : answered ? "call_started seq_call_trying_endpoints call_accepted call_hangup" : "call_started seq_call_trying_endpoints call_hangup";
        return $$"""
            {"uuid": "{{uuid}}", "time": "{{time}}", "direction": "{{direction}}", "status": "accepted", "duration": 30, "from": "{{from}}", "to": "{{to}}",
             "call_events": [{{string.Join(",", events.Split(' ').Select(e => $$"""{"time": "{{time}}", "event": "{{e}}"}"""))}}]}
            """;
    }

    [Theory]
    [InlineData("known", ContactNumber, null, null, true)]           // A contact's number: someone known.
    [InlineData("known", "+447700900123", null, null, false)]       // A stranger.
    [InlineData("abroad", "+390612345678", null, null, true)]       // An Italian number, at a British site.
    [InlineData("abroad", "+447700900123", null, null, false)]
    [InlineData("quality", "+447700900123", 40, null, true)]        // Scored 40, under 70.
    [InlineData("quality", "+447700900123", 90, null, false)]
    [InlineData("menu", "+447700900123", null, 42, true)]           // Chose the option the flow asks about.
    [InlineData("menu", "+447700900123", null, 7, false)]           // Chose another.
    public async Task The_newer_conditions_are_judged_on_the_call_as_TalkWatch_stored_it(string which, string from, int? quality, int? chose, bool alerted)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await h.PollAsync();
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        FlowCondition condition = which switch
        {
            "known" => new KnownCallerCondition(true),
            "abroad" => new AbroadCondition(true),
            "quality" => new QualityCondition(70),
            _ => new MenuOptionCondition([42]),
        };
        await h.SendToAsync("Judged", AlertEventType.MissedCall, [desk], condition);

        var time = LongAfterTheFixtures.AddMinutes(-2).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var menu = chose is { } item ? $$$""", {"time": "{{{time}}}", "event": "entered_sa_menu", "event_data": {"sa_id": 1, "sa_item_id": {{{item}}}, "sa_item_key": 2, "sa_item_title": "Sales"}}""" : "";
        var score = quality is { } q ? $", \"quality_score\": {q}" : "";
        h.Console.AddCall($$$"""
            {"uuid": "judged", "time": "{{{time}}}", "direction": "in", "status": "accepted", "duration": 30, "from": "{{{from}}}", "to": "+441144960042"{{{score}}},
             "call_events": [{"time": "{{{time}}}", "event": "call_started"}{{{menu}}}, {"time": "{{{time}}}", "event": "seq_call_trying_endpoints"}, {"time": "{{{time}}}", "event": "call_hangup"}]}
            """);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:judged:{AlertEventType.MissedCall}", Ct));
        Assert.Equal(alerted ? 1 : 0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.EventId == alert.Id, Ct)));
    }

    [Fact]
    public async Task A_caller_with_no_name_of_their_own_is_named_from_the_contacts_in_alerts_on_the_calls_page_and_with_their_history()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [desk]);
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await h.PollAsync();

        // They rang and got through, were rung back, then rang again and were missed just now.
        var missedAt = LongAfterTheFixtures.AddMinutes(-2);
        h.Console.AddCall(ContactCall("contact-answered", missedAt.AddDays(-2), "in", answered: true));
        h.Console.AddCall(ContactCall("contact-rung", missedAt.AddDays(-1), "out", answered: false));
        h.Console.AddCall(ContactCall("contact-missed", missedAt, "in", answered: false));
        await h.PollAsync();
        await h.DispatchAsync();

        var sent = Assert.Single(h.Receiver.Requests);
        using (var body = System.Text.Json.JsonDocument.Parse(sent.Body))
        {
            Assert.StartsWith($"From {ContactName} ({ContactNumber})", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        var calls = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct));
        Assert.Contains($"{ContactName} ({ContactNumber})", calls, StringComparison.Ordinal);

        var page = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri("/calls/contact-missed", UriKind.Relative), Ct));
        Assert.Contains($"Call from {ContactName} ({ContactNumber})", page, StringComparison.Ordinal);
        // Every other call with the number counts, the captured ones too: a forwarding mobile has plenty.
        var others = await h.DbAsync(db => db.Calls.Where(c => c.TalkUuid != "contact-missed"
            && ((c.Direction == "in" && c.FromE164 == ContactNumber) || (c.Direction == "out" && c.ToE164 == ContactNumber))).ToListAsync(Ct));
        var rang = others.Where(c => c.Direction == "in").ToList();
        Assert.Contains(
            $"{rang.Count} rang in ({rang.Count(c => c.Outcome == Core.Calls.CallOutcome.Answered)} answered, "
            + $"{rang.Count(c => c.Outcome is Core.Calls.CallOutcome.Missed or Core.Calls.CallOutcome.Voicemail)} missed or voicemail), {others.Count(c => c.Direction == "out")} called out.",
            page, StringComparison.Ordinal);
        Assert.Contains("data-history-call=\"contact-answered\"", page, StringComparison.Ordinal);
        Assert.Contains("data-history-call=\"contact-rung\"", page, StringComparison.Ordinal);
        Assert.Equal(ContactName, await h.DbAsync(db => db.CallerNames.Where(n => n.Number == ContactNumber).Select(n => n.Name).SingleAsync(Ct)));
    }
}

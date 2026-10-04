using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    // A call Talk puts through to an outside phone, as the live feed reports it: ringing at first, then the outside
    // contact tried, then hung up.
    /// <param name="answeredBy">Who answered: an outside contact's uuid, <c>desk</c> for a phone on the system, or nobody.</param>
    private static LiveMessage OutsideCall(string uuid, DateTimeOffset at, string? contact, bool ended, string? answeredBy = null)
    {
        var time = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var events = new List<string> { $$$"""{"time": "{{{time}}}", "event": "call_started", "event_data": {"to": "+441144960042", "from": "+447700900123"}}""" };
        if (contact is not null)
        {
            events.Add($$$"""{"time": "{{{time}}}", "event": "seq_call_trying_endpoints", "event_data": {"contact_uuids": ["{{{contact}}}"]}}""");
        }

        if (answeredBy == "desk")
        {
            events.Add($$$"""{"time": "{{{time}}}", "event": "call_accepted", "event_data": {"accepted_by": "101"}}""");
        }
        else if (answeredBy is not null)
        {
            events.Add($$$"""{"time": "{{{time}}}", "event": "call_accepted", "event_data": {"accepted_by": "+447700900880", "accepted_by_contact_id": 99, "accepted_by_contact_uuid": "{{{answeredBy}}}"}}""");
        }

        if (ended)
        {
            events.Add($$$"""{"time": "{{{time}}}", "event": "call_hangup", "event_data": {"hangup_cause": "normal_end"}}""");
        }

        return LiveMessage.Parse($$$"""
            {"event": "CALL_LOG_UPDATED", "data": {"records": [{"uuid": "{{{uuid}}}", "time": "{{{time}}}", "direction": "in", "status": "{{{(ended ? "cancelled" : "ringing")}}}",
             "from": "+447700900123", "to": "+441144960042", "call_events": [{{{string.Join(",", events)}}}]}]}}
            """)!;
    }

    /// <summary>
    /// A viewer with no grant on any line, who carries one of Talk's outside contacts' phones, with a webhook and an email
    /// channel of their own; and a flow that tells whoever a call is put through to outside.
    /// </summary>
    private async Task<(Harness H, string Contact, Guid Carrier, Guid Hook, Guid Mail)> OutsidePhoneAsync(DateTimeOffset now, bool urgent = false, params (string Name, string Value)[] quiet)
    {
        var h = await StartAsync(now);
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        var contact = h.App.Services.GetRequiredService<LineDirectorySync>().Current.Contacts.First(c => c.Uuid is { Length: > 0 }).Uuid!;
        var carrier = await h.AddViewerAsync("carrier");
        await PostAsync(h.Admin, $"/admin/users/{carrier}", $"/admin/users/{carrier}/outside-contacts", ("Contacts", contact));
        var hook = await h.AddChannelAsync("Carrier's phone", ChannelKind.Webhook, "https://hooks.test/carrier", owner: carrier.ToString(), more: quiet);
        var mail = await h.AddChannelAsync("Carrier's mail", ChannelKind.Email, "carrier@example.test", owner: carrier.ToString());
        var saved = await h.AddFlowAsync("Put through outside", new FlowDefinition
        {
            Trigger = AlertEventType.InboundCall,
            Conditions = [new ForwardedOutsideCondition(true)],
            Steps = [new NotifyStep([FlowRecipient.ToOutside(OutsideScope.ThisCall)], urgent, NotifyIncludes.Summary | NotifyIncludes.Transcript)],
        });
        Assert.Contains("Flow%20saved", saved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(1, await h.DbAsync(db => db.AlertFlows.CountAsync(Ct)));
        return (h, contact, carrier, hook, mail);
    }

    [Fact]
    public async Task A_call_put_through_to_an_outside_phone_tells_whoever_carries_it_while_it_rings_with_only_the_caller_and_line()
    {
        var (h, contact, _, hook, _) = await OutsidePhoneAsync(LongAfterTheFixtures);
        await using var _ = h;
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Every call", AlertEventType.InboundCall, [desk]);
        var listener = h.App.Services.GetRequiredService<LiveListener>();
        var at = LongAfterTheFixtures.AddMinutes(-1);

        // Ringing at the switchboard: an incoming call, not yet put through anywhere.
        await listener.HandleAsync(OutsideCall("outside-1", at, contact: null, ended: false), Ct);
        Assert.False(await h.DbAsync(db => db.AlertDeliveries.AnyAsync(d => d.ChannelId == hook, Ct)));

        // Talk puts it through to the outside phone, which rings.
        await listener.HandleAsync(OutsideCall("outside-1", at, contact, ended: false), Ct);
        await listener.HandleAsync(OutsideCall("outside-1", at, contact, ended: false), Ct);
        var forwarded = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:outside-1:{AlertService.ForwardedKey}", Ct));
        var delivery = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(d => d.EventId == forwarded.Id, Ct));
        Assert.Equal((hook, true, true, 0), (delivery.ChannelId, delivery.Outside, delivery.CallerOnly, delivery.Include));

        // The flow for every call ran once, when the call came in, not again when it was put through.
        Assert.Equal(1, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.ChannelId == desk, Ct)));

        await h.DispatchAsync();
        var sent = Assert.Single(h.Receiver.Requests, r => r.Uri.Host == "hooks.test" && r.Uri.AbsolutePath == "/carrier");
        using var body = JsonDocument.Parse(sent.Body);
        var message = body.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("From +447700900123", message, StringComparison.Ordinal);
        Assert.Contains("to +441144960042", message, StringComparison.Ordinal);
        Assert.Contains("put through to", message, StringComparison.Ordinal);
        Assert.DoesNotContain("has ended", message, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("links").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("call").GetProperty("uuid").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("transcript").ValueKind);
    }

    [Fact]
    public async Task A_carrier_who_may_see_the_line_hears_of_it_as_of_any_alert()
    {
        var (h, contact, _, _, _) = await OutsidePhoneAsync(LongAfterTheFixtures);
        await using var _ = h;
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        await PostAsync(h.Admin, $"/admin/users/{admin}", $"/admin/users/{admin}/outside-contacts", ("Contacts", contact));
        var mine = await h.AddChannelAsync("Admin's phone", ChannelKind.Webhook, "https://hooks.test/admin", owner: admin.ToString());

        await h.App.Services.GetRequiredService<LiveListener>().HandleAsync(OutsideCall("outside-2", LongAfterTheFixtures.AddMinutes(-1), contact, ended: false), Ct);

        var delivery = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(d => d.ChannelId == mine, Ct));
        Assert.Equal((true, false), (delivery.Outside, delivery.CallerOnly));
        Assert.Equal((int)(NotifyIncludes.Summary | NotifyIncludes.Transcript), delivery.Include);
        await h.DispatchAsync();
        using var body = JsonDocument.Parse(Assert.Single(h.Receiver.Requests, r => r.Uri.AbsolutePath == "/admin").Body);
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("links").ValueKind);
        Assert.Equal("outside-2", body.RootElement.GetProperty("call").GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task Never_by_email_and_only_to_whoever_carries_the_phone_it_was_put_through_to()
    {
        var (h, contact, _, hook, mail) = await OutsidePhoneAsync(LongAfterTheFixtures);
        await using var _ = h;
        var other = h.App.Services.GetRequiredService<LineDirectorySync>().Current.Contacts.First(c => c.Uuid is { Length: > 0 } && c.Uuid != contact).Uuid!;
        var elsewhere = await h.AddViewerAsync("elsewhere");
        await PostAsync(h.Admin, $"/admin/users/{elsewhere}", $"/admin/users/{elsewhere}/outside-contacts", ("Contacts", other));
        var theirs = await h.AddChannelAsync("Elsewhere's phone", ChannelKind.Webhook, "https://hooks.test/elsewhere", owner: elsewhere.ToString());

        await h.App.Services.GetRequiredService<LiveListener>().HandleAsync(OutsideCall("outside-3", LongAfterTheFixtures.AddMinutes(-1), contact, ended: false), Ct);

        var to = await h.DbAsync(db => db.AlertDeliveries.Select(d => d.ChannelId).ToListAsync(Ct));
        Assert.Equal([hook], to);
        Assert.DoesNotContain(mail, to);
        Assert.DoesNotContain(theirs, to);
    }

    [Fact]
    public async Task A_call_only_seen_once_it_has_ended_still_tells_them_saying_so()
    {
        var (h, contact, _, hook, _) = await OutsidePhoneAsync(LongAfterTheFixtures);
        await using var _ = h;

        await h.App.Services.GetRequiredService<LiveListener>().HandleAsync(OutsideCall("outside-4", LongAfterTheFixtures.AddMinutes(-3), contact, ended: true), Ct);
        await h.DispatchAsync();

        using var body = JsonDocument.Parse(Assert.Single(h.Receiver.Requests, r => r.Uri.AbsolutePath == "/carrier").Body);
        Assert.EndsWith("The call has ended", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.ChannelId == hook, Ct)));
    }

    [Theory]
    [InlineData(false, DeliveryState.Cancelled)]
    [InlineData(true, DeliveryState.Sent)]
    public async Task In_quiet_hours_word_of_it_is_dropped_rather_than_held_unless_the_step_is_urgent(bool urgent, DeliveryState state)
    {
        // Wednesday 23:00 in London; the carrier's phone is quiet every night 22:00 to 07:00.
        var night = DateTimeOffset.Parse("2026-09-30T22:00:00Z", CultureInfo.InvariantCulture);
        var (h, contact, _, hook, _) = await OutsidePhoneAsync(night, urgent,
            [.. Enum.GetValues<DayOfWeek>().Select(d => ("QuietDays", d.ToString())), ("QuietStart", "22:00"), ("QuietEnd", "07:00")]);
        await using var _ = h;

        await h.App.Services.GetRequiredService<LiveListener>().HandleAsync(OutsideCall("outside-5", night.AddMinutes(-1), contact, ended: false), Ct);
        await h.DispatchAsync();

        var delivery = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(d => d.ChannelId == hook, Ct));
        Assert.Equal(state, delivery.State);
        Assert.Equal(urgent ? null : AlertDispatcher.QuietDropped, delivery.LastError);
        Assert.Equal(urgent ? 1 : 0, h.Receiver.Requests.Count(r => r.Uri.AbsolutePath == "/carrier"));
    }

    [Theory]
    [InlineData("desk", 1)]     // A phone on the system answered: the carrier hears it is in hand.
    [InlineData("theirs", 0)]   // Their own phone answered: they know already.
    public async Task Once_someone_else_answers_whoever_carries_the_phone_hears_it_is_in_hand(string answeredBy, int toldAgain)
    {
        var (h, contact, _, hook, _) = await OutsidePhoneAsync(LongAfterTheFixtures);
        await using var _ = h;
        var listener = h.App.Services.GetRequiredService<LiveListener>();
        var at = LongAfterTheFixtures.AddMinutes(-1);
        await listener.HandleAsync(OutsideCall("outside-6", at, contact, ended: false), Ct);
        await h.DispatchAsync();

        await listener.HandleAsync(OutsideCall("outside-6", at, contact, ended: false, answeredBy == "desk" ? "desk" : contact), Ct);
        await h.DispatchAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:outside-6:{AlertService.ForwardedKey}", Ct));
        Assert.Equal(AlertService.ByAnswered, alert.AcknowledgedBy);
        var words = await h.DbAsync(db => db.AlertDeliveries.Where(d => d.ChannelId == hook && d.Acknowledgement).ToListAsync(Ct));
        Assert.Equal(toldAgain, words.Count);
        Assert.All(words, w => Assert.True(w.CallerOnly));
        var sent = h.Receiver.Requests.Where(r => r.Uri.AbsolutePath == "/carrier").ToList();
        Assert.Equal(1 + toldAgain, sent.Count);
        if (toldAgain > 0)
        {
            using var body = JsonDocument.Parse(sent[^1].Body);
            Assert.Contains("someone else answered", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("call").GetProperty("uuid").ValueKind);
        }
    }

    [Fact]
    public async Task Outside_phones_are_linked_from_either_side_and_a_contact_gone_from_Talk_loses_its_links()
    {
        var (h, contact, carrier, _, _) = await OutsidePhoneAsync(LongAfterTheFixtures);
        await using var _ = h;
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);

        // From the contact's side: a shared on-call mobile, carried by both.
        await PostAsync(h.Admin, "/admin/outside-phones", "/admin/outside-phones", ("Contact", contact), ("People", carrier.ToString()), ("People", admin.ToString()));
        Assert.Equivalent(new[] { carrier, admin }, await h.DbAsync(db => db.ContactLinks.Where(l => l.ContactUuid == contact).Select(l => l.UserId).ToListAsync(Ct)));
        var page = await h.Admin.GetStringAsync(new Uri("/admin/outside-phones", UriKind.Relative), Ct);
        Assert.Contains($"data-outside-phone=\"{contact}\" data-carriers=\"2\"", page, StringComparison.Ordinal);

        // From the person's side: ticking none takes them off it.
        var untick = await PostAsync(h.Admin, $"/admin/users/{carrier}", $"/admin/users/{carrier}/outside-contacts");
        Assert.Equal(System.Net.HttpStatusCode.Redirect, untick.StatusCode);
        Assert.Equal([admin], await h.DbAsync(db => db.ContactLinks.Select(l => l.UserId).ToListAsync(Ct)));

        // Talk no longer has the contact: its link goes. An account that lists no contacts at all drops nothing.
        var directory = h.App.Services.GetRequiredService<LineDirectorySync>().Current;
        Assert.Equal(0, await h.DbAsync(db => LineDirectorySync.DropGoneContactLinksAsync(db, LineDirectory.Empty, Ct)));
        var without = new LineDirectory([], [], []) { Contacts = [.. directory.Contacts.Where(c => c.Uuid != contact)] };
        Assert.Equal(1, await h.DbAsync(db => LineDirectorySync.DropGoneContactLinksAsync(db, without, Ct)));
        Assert.False(await h.DbAsync(db => db.ContactLinks.AnyAsync(Ct)));
    }

    [Fact]
    public async Task Only_an_admin_names_the_people_behind_outside_phones_and_only_on_call_alerts()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var outside = new NotifyStep([FlowRecipient.ToOutside(OutsideScope.AnyLinked)]);

        var drift = await h.AddFlowAsync("Drift outside", new FlowDefinition { Trigger = AlertEventType.Drift, Steps = [outside] });
        Assert.DoesNotContain("Flow%20saved", drift.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var (manager, _) = await ManagerAsync(h, "Did:+441144960042");
        var theirs = await h.AddFlowAsync("Manager outside", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [outside] }, manager);
        Assert.DoesNotContain("Flow%20saved", theirs.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await h.DbAsync(db => db.AlertFlows.CountAsync(Ct)));

        await h.AddFlowAsync("Missed outside", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [outside] });
        Assert.Equal(1, await h.DbAsync(db => db.AlertFlows.CountAsync(Ct)));
    }
}

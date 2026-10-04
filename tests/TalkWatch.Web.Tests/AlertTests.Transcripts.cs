using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    [Fact]
    public async Task A_call_rated_negative_alerts_once_without_saying_what_was_said()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Unhappy callers", AlertEventType.NegativeCall, [desk]);
        await h.PollAsync();
        var call = h.Console.Calls().First(c => c.Direction == "in");

        // A finished transcript as the live feed sends it, for that call, rated negative.
        var message = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureConsole.DefaultDirectory, "0184-ws-proxy-talk.json"), Ct))!;
        message["data"]!["context"]!["call_uuid"] = call.Uuid;
        message["data"]!["analytics"]!["sentimentClassification"] = "negative";
        var summary = (string)message["data"]!["analytics"]!["summary"]!;
        h.Clock.Now = call.Time + TimeSpan.FromMinutes(30);
        var listener = h.App.Services.GetRequiredService<LiveListener>();
        await listener.HandleAsync(LiveMessage.Parse(message.ToJsonString())!, Ct);
        await listener.HandleAsync(LiveMessage.Parse(message.ToJsonString())!, Ct);
        await h.DispatchAsync();

        var sent = Assert.Single(h.Receiver.Requests);
        Assert.Equal(nameof(AlertEventType.NegativeCall), sent.Headers["X-TalkWatch-Event"]);
        var body = Encoding.UTF8.GetString(sent.Body);
        Assert.Contains("rated it negative", body, StringComparison.Ordinal);
        Assert.DoesNotContain(summary[..Math.Min(30, summary.Length)], body, StringComparison.Ordinal);
        Assert.True(await h.DbAsync(db => db.CallTranscripts.AnyAsync(t => db.Calls.Any(c => c.Id == t.CallId && c.TalkUuid == call.Uuid), Ct)));
    }

    [Theory]
    [InlineData("Hi, it's Sam from Kestrel. The boiler's gone, it's urgent, please ring me back.", true)]
    [InlineData("Hi, it's Sam from Kestrel, just ringing to say thanks for yesterday.", false)]
    public async Task A_transcribed_voicemail_starts_a_flow_on_what_it_says_without_sending_what_it_says(string said, bool alerted)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Urgent voicemail", AlertEventType.VoicemailTranscribed, [desk], new TranscriptWordsCondition(["urgent", "burst pipe"]));
        await h.PollAsync();

        // A voicemail left just now.
        var time = LongAfterTheFixtures.AddMinutes(-3).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        h.Console.AddCall($$$"""
            {"uuid": "left-a-message", "time": "{{{time}}}", "direction": "in", "status": "accepted", "duration": 40, "from": "+447700900123", "to": "+441144960042",
             "call_events": [{"time": "{{{time}}}", "event": "call_started"}, {"time": "{{{time}}}", "event": "seq_call_trying_endpoints"},
               {"time": "{{{time}}}", "event": "call_sent_to_voicemail"}, {"time": "{{{time}}}", "event": "vm_msg_recorded"}, {"time": "{{{time}}}", "event": "call_hangup"}]}
            """);
        await h.PollAsync();

        // Its transcript, a minute later, as the live feed sends it: twice, as Talk sometimes does.
        var message = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureConsole.DefaultDirectory, "0184-ws-proxy-talk.json"), Ct))!;
        message["data"]!["context"]!["call_uuid"] = "left-a-message";
        message["data"]!["analytics"]!["sentimentClassification"] = "neutral";
        message["data"]!["analytics"]!["lines"]![0]!["line"] = said;
        h.Clock.Now = LongAfterTheFixtures.AddMinutes(-2);
        var listener = h.App.Services.GetRequiredService<LiveListener>();
        await listener.HandleAsync(LiveMessage.Parse(message.ToJsonString())!, Ct);
        await listener.HandleAsync(LiveMessage.Parse(message.ToJsonString())!, Ct);
        await h.DispatchAsync();

        var sent = h.Receiver.Requests.Where(r => r.Headers.GetValueOrDefault("X-TalkWatch-Event") == nameof(AlertEventType.VoicemailTranscribed)).ToList();
        Assert.Equal(alerted ? 1 : 0, sent.Count);
        foreach (var request in sent)
        {
            var body = Encoding.UTF8.GetString(request.Body);
            Assert.Contains("Voicemail transcribed", body, StringComparison.Ordinal);
            // Nothing that was said: not the words the flow looked for (the webhook's "urgent" field is its own), nor the rest.
            Assert.DoesNotContain("boiler", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("it's urgent", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ring me back", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_manager_cannot_build_a_flow_that_reads_what_voicemails_say()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var line = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.Did).Select(l => l.Key).FirstAsync(Ct));
        var (manager, managerId) = await ManagerAsync(h, $"Did:{line}");
        using var _ = manager;

        var response = await h.AddFlowAsync("Snooping", new FlowDefinition
        {
            Trigger = AlertEventType.VoicemailTranscribed,
            Conditions = [new TranscriptWordsCondition(["urgent"])],
            Steps = [new NotifyStep([FlowRecipient.ToPerson(managerId)])],
        }, manager);

        Assert.Contains("for%20admins", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(0, await h.DbAsync(db => db.AlertFlows.CountAsync(Ct)));
    }

    [Fact]
    public async Task An_old_call_rated_negative_when_transcripts_are_first_copied_alerts_on_nothing()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Unhappy callers", AlertEventType.NegativeCall, [desk]);
        await h.PollAsync();
        var call = h.Console.Calls().First(c => c.Direction == "in");
        var message = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(FixtureConsole.DefaultDirectory, "0184-ws-proxy-talk.json"), Ct))!;
        message["data"]!["context"]!["call_uuid"] = call.Uuid;
        message["data"]!["analytics"]!["sentimentClassification"] = "negative";

        // Two days after the call: the transcript is kept, but it is too late to alert on.
        h.Clock.Now = call.Time + TimeSpan.FromDays(2);
        await h.App.Services.GetRequiredService<LiveListener>().HandleAsync(LiveMessage.Parse(message.ToJsonString())!, Ct);
        await h.DispatchAsync();

        Assert.Empty(h.Receiver.Requests);
        Assert.True(await h.DbAsync(db => db.CallTranscripts.AnyAsync(t => db.Calls.Any(c => c.Id == t.CallId && c.TalkUuid == call.Uuid), Ct)));
    }
}

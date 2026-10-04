using System.Globalization;
using System.Text.Json;
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
    private const string Install = "/proxy/talk/api/install";
    private const string Config = "/proxy/talk/api/setting/config";

    /// <summary>A captured response with some fields changed, served in its place.</summary>
    private static void Serve(Harness h, string path, string file, params (string Field, JsonNode? Value)[] changes)
    {
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureConsole.DefaultDirectory, file)))!;
        foreach (var (field, value) in changes)
        {
            json[field] = value;
        }

        h.Console.Overrides[path] = json.ToJsonString();
    }

    private static string[] Messages(Harness h) =>
        [.. h.Receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("message").GetString()!)];

    [Fact]
    public async Task A_problem_with_the_talk_account_alerts_once_when_it_appears_and_again_if_it_comes_back()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Account", AlertEventType.AccountProblem, [desk]);
        var health = h.App.Services.GetRequiredService<ConsoleHealthMonitor>();
        await h.PollAsync();

        // As captured: all well, so nothing.
        await health.CheckAsync(Ct);
        Serve(h, Install, "0015-http-proxy-talk-api-install.json", ("payment_status", "failed"));
        await health.CheckAsync(Ct);
        h.Clock.Now += TimeSpan.FromHours(1);
        await health.CheckAsync(Ct);
        Serve(h, Install, "0015-http-proxy-talk-api-install.json");
        h.Clock.Now += TimeSpan.FromHours(1);
        await health.CheckAsync(Ct);
        Serve(h, Install, "0015-http-proxy-talk-api-install.json", ("payment_status", "failed"), ("blocked", true));
        h.Clock.Now += TimeSpan.FromHours(1);
        await health.CheckAsync(Ct);
        await h.DispatchAsync();
        Assert.Equal(["The Talk account is blocked.", "The last payment failed.", "The last payment failed."], Messages(h).Order(StringComparer.Ordinal));
        Assert.All(h.Receiver.Requests, r => Assert.Equal(nameof(AlertEventType.AccountProblem), r.Headers["X-TalkWatch-Event"]));
    }

    [Fact]
    public async Task Recording_switched_off_alerts_once_but_off_when_first_seen_does_not()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Settings", AlertEventType.SettingTurnedOff, [desk]);
        var health = h.App.Services.GetRequiredService<ConsoleHealthMonitor>();

        // Transcription is off when first seen (the poll's own hourly check): perhaps on purpose, so nothing. Recording is
        // on, then switched off.
        Serve(h, Config, "0032-http-proxy-talk-api-setting-config.json", ("ai_call_transcriptions_enabled", false));
        await h.PollAsync();
        Serve(h, Config, "0032-http-proxy-talk-api-setting-config.json", ("ai_call_transcriptions_enabled", false), ("call_log_recording_enabled", false));
        h.Clock.Now += TimeSpan.FromHours(1);
        await health.CheckAsync(Ct);
        h.Clock.Now += TimeSpan.FromHours(1);
        await health.CheckAsync(Ct);
        await h.DispatchAsync();

        var sent = Assert.Single(h.Receiver.Requests);
        Assert.StartsWith("Call recording has been switched off", Messages(h)[0], StringComparison.Ordinal);
        Assert.Equal(nameof(AlertEventType.SettingTurnedOff), sent.Headers["X-TalkWatch-Event"]);
    }

    [Fact]
    public async Task A_handset_that_loses_its_registration_or_gets_an_update_alerts_those_it_belongs_to()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Unregistered", AlertEventType.HandsetUnregistered, [desk]);
        await h.SendToAsync("Updates", AlertEventType.HandsetUpdateAvailable, [desk]);
        var listener = h.App.Services.GetRequiredService<LiveListener>();

        LiveMessage Device(bool registered, bool update) => LiveMessage.Parse(
            $$"""{"event":"DEVICES_UPDATED","data":[{"mac":"aabbccddeeff","model":"UTP-G3","display_name":"Front desk","status":"online","sip_reg":{{(registered ? "true" : "false")}},"update_available":{{(update ? "true" : "false")}}}]}""")!;
        await listener.HandleAsync(Device(registered: true, update: false), Ct);
        await listener.HandleAsync(Device(registered: false, update: false), Ct);
        h.Clock.Now += TimeSpan.FromMinutes(5);
        await listener.HandleAsync(Device(registered: false, update: true), Ct);
        await listener.HandleAsync(Device(registered: false, update: true), Ct);
        await h.DispatchAsync();

        Assert.Equal(["Front desk has a firmware update waiting at", "Front desk lost its registration and can't make or take calls at"],
            Messages(h).Select(m => m[..m.LastIndexOf(' ')]).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_recent_call_talk_scored_low_for_quality_alerts_once()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Poor quality", AlertEventType.PoorQualityCall, [desk]);
        await h.PollAsync();
        var at = LongAfterTheFixtures.AddMinutes(-3);
        var time = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        h.Console.AddCall($$"""
            {"uuid": "crackly", "time": "{{time}}", "direction": "in", "status": "accepted", "duration": 120, "quality_score": 41,
             "from": "+447700900123", "to": "+441144960042",
             "call_events": [{"time": "{{time}}", "event": "call_started"}, {"time": "{{time}}", "event": "call_accepted"}, {"time": "{{time}}", "event": "call_hangup"}]}
            """);

        await h.PollAsync();
        await h.PollAsync();
        await h.DispatchAsync();

        var sent = Assert.Single(h.Receiver.Requests);
        Assert.Equal(nameof(AlertEventType.PoorQualityCall), sent.Headers["X-TalkWatch-Event"]);
        Assert.EndsWith("scored 41 of 100 for quality.", Messages(h)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Alerts_about_the_whole_site_are_for_admins_and_on_no_line()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.AccountProblem,
            Conditions = [new LineCondition([new(Core.Calls.LineKind.Did, "+441144960042")])],
            Steps = [new NotifyStep([FlowRecipient.ToChannel(await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk"))])],
        };

        Assert.Equal("An alert about the whole site is on no line.", Flows.Problem(flow));
        Assert.True(Flows.IsSiteAlert(AlertEventType.SettingTurnedOff));
        Assert.False(Flows.IsSiteAlert(AlertEventType.HandsetUnregistered));
        await h.AddFlowAsync("Lined", flow);
        Assert.Equal(0, await h.DbAsync(db => db.AlertFlows.CountAsync(Ct)));
    }
}

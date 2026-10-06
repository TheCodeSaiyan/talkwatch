using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Managing every alert is not a way round what someone may read or hear: a channel that belongs to nobody carries
/// what the whole site holds, and the mail settings hold the site's mail password.
/// </summary>
public sealed partial class AlertTests
{
    /// <summary>Someone whose role manages every alert and does nothing else, signed in.</summary>
    private static async Task<HttpClient> AlertDeskAsync(Harness h)
    {
        await PostAsync(h.Admin, "/admin/roles", "/admin/roles", ("Name", "Alert desk"));
        var role = await h.DbAsync(async db => (await db.Roles.SingleAsync(r => r.Name == "Alert desk", Ct)).Id);
        await PostAsync(h.Admin, "/admin/roles", $"/admin/roles/{role}/permissions", ("Permissions", nameof(Permission.ManageAlerts)));
        await PostAsync(h.Admin, "/admin/users", "/admin/users", ("Username", "desk"), ("Role", "Alert desk"), ("Password", ViewerPassword));
        var browser = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(browser, "desk", ViewerPassword);
        return browser;
    }

    [Fact]
    public async Task Managing_every_alert_sends_no_transcript_or_voicemail_the_manager_may_not_have()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        using var desk = await AlertDeskAsync(h);
        var site = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/office");

        foreach (var include in new[] { NotifyIncludes.Summary, NotifyIncludes.Transcript, NotifyIncludes.Voicemail })
        {
            await h.AddFlowAsync($"With {include}", new FlowDefinition
            {
                Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToChannel(site)], Include: include)],
            }, desk);
        }

        var plain = await h.AddFlowAsync("Just the call", new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToChannel(site)])],
        }, desk);

        Assert.Equal(["Just the call"], await h.DbAsync(db => db.AlertFlows.Select(f => f.Name).ToListAsync(Ct)));
        Assert.Contains("Flow%20saved", plain.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_an_admin_changes_the_mail_and_telegram_settings()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        using var desk = await AlertDeskAsync(h);
        await SaveSettingsAsync(h, ("SmtpHost", "mail.example.test"), ("SmtpFrom", "talkwatch@example.test"), ("SmtpPassword", "the site's password"));

        await PostAsync(desk, "/admin/alerts", "/admin/alerts/settings", ("SmtpHost", "mail.attacker.test"), ("SmtpFrom", "talkwatch@example.test"));

        Assert.Equal("mail.example.test", await h.DbAsync(db => db.AlertSettings.Select(s => s.SmtpHost).SingleAsync(Ct)));
    }
}

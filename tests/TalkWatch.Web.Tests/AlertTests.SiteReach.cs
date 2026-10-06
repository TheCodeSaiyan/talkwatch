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

    // The saved password, or the one in the settings, would otherwise go to whatever mail server an admin typed.
    [Fact]
    public async Task Changing_the_mail_server_needs_its_password_again()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await SaveSettingsAsync(h, ("SmtpHost", "mail.example.test"), ("SmtpFrom", "talkwatch@example.test"), ("SmtpUsername", "talkwatch"), ("SmtpPassword", "the site's password"));

        var moved = await SaveSettingsAsync(h, ("SmtpHost", "mail.elsewhere.test"), ("SmtpFrom", "talkwatch@example.test"), ("SmtpUsername", "talkwatch"));

        Assert.Contains("password%20again", moved.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal("mail.example.test", await h.DbAsync(db => db.AlertSettings.Select(s => s.SmtpHost).SingleAsync(Ct)));
    }

    // A token sent to ntfy over plain http could be read on the way.
    [Fact]
    public async Task An_ntfy_token_goes_only_over_https()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);

        await PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/channels",
            ("Name", "Plain"), ("Kind", nameof(ChannelKind.Ntfy)), ("Target", "http://ntfy.example.test/alerts"), ("Secret", "tk_secret"), ("Owner", ""));
        await PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/channels",
            ("Name", "Open"), ("Kind", nameof(ChannelKind.Ntfy)), ("Target", "http://ntfy.example.test/open"), ("Secret", ""), ("Owner", ""));

        Assert.Equal(["Open"], await h.DbAsync(db => db.AlertChannels.Select(c => c.Name).ToListAsync(Ct)));
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

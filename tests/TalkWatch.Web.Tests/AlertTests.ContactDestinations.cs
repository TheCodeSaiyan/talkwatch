using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>Talk's contacts as flow destinations, at what Talk holds for them: their email, for now.</summary>
public sealed partial class AlertTests
{
    private const string Locum = "contact-locum";
    private const string NumberOnly = "ac7a4901-2722-4e41-9f20-87f95df72cb2";

    private static string Contacts(string locumEmail) => $$"""
        [{"id": 9, "uuid": "{{Locum}}", "first_name": "Robin", "last_name": "Locum", "email": "{{locumEmail}}", "phone_numbers": [{"did": "+447700900111", "label": "mobile"}]},
         {"id": 3, "uuid": "{{NumberOnly}}", "first_name": "Morgan", "last_name": "", "email": "", "phone_numbers": [{"did": "+447700900210", "label": "mobile"}]}]
        """;

    [Fact]
    public async Task A_flow_emails_a_contact_at_the_address_talk_holds_and_follows_it_when_it_changes()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await StartAsync(LongAfterTheFixtures, s => s.Configure<SmtpOptions>(o =>
        {
            (o.Host, o.Port, o.From, o.StartTls) = ("127.0.0.1", smtp.Port, "talkwatch@example.test", false);
        }));
        h.Console.Overrides["/proxy/talk/api/contacts"] = Contacts("robin@example.test");
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await h.AddFlowAsync("Tell the locum", new FlowDefinition { Trigger = AlertEventType.Drift, Steps = [new NotifyStep([FlowRecipient.ToContact(Locum)])] });

        await DriftAsync(h);

        var mail = Assert.Single(smtp.Messages);
        Assert.Equal(["robin@example.test"], mail.To);
        var kept = await h.DbAsync(db => db.AlertChannels.SingleAsync(c => c.ContactUuid == Locum, Ct));
        Assert.Equal((ChannelKind.Email, "Robin Locum (contact)"), (kept.Kind, kept.Name));
        // Kept for the contact, so not listed with the channels people set up.
        Assert.DoesNotContain("Robin Locum (contact)", await h.Admin.GetStringAsync(new Uri("/admin/alerts", UriKind.Relative), Ct), StringComparison.Ordinal);

        // Talk's address changes: the next alert goes to the new one, on the same channel.
        h.Console.Overrides["/proxy/talk/api/contacts"] = Contacts("robin.locum@example.test");
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await h.DbAsync(async db => await db.AlertEvents.ExecuteUpdateAsync(e => e.SetProperty(a => a.AcknowledgedAt, h.Clock.Now), Ct));
        h.Console.Drifted = false;
        await h.PollAsync();
        h.Clock.Now += TimeSpan.FromHours(2);
        await DriftAsync(h);

        Assert.Equal(["robin.locum@example.test"], smtp.Messages.Last().To);
        Assert.Equal(1, await h.DbAsync(db => db.AlertChannels.CountAsync(c => c.ContactUuid == Locum, Ct)));
    }

    [Fact]
    public async Task A_contact_with_no_email_or_named_by_someone_not_an_admin_is_refused()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        h.Console.Overrides["/proxy/talk/api/contacts"] = Contacts("robin@example.test");
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);

        await h.AddFlowAsync("Number only", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToContact(NumberOnly)])] });

        Assert.False(await h.DbAsync(db => db.AlertFlows.AnyAsync(f => f.Name == "Number only", Ct)));

        await PostAsync(h.Admin, "/admin/users", "/admin/users", ("Username", "manager"), ("Role", Roles.Manager), ("Password", ViewerPassword));
        using var manager = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(manager, "manager", ViewerPassword);
        await h.AddFlowAsync("Manager's", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToContact(Locum)])] }, manager);

        Assert.False(await h.DbAsync(db => db.AlertFlows.AnyAsync(f => f.Name == "Manager's", Ct)));
    }
}

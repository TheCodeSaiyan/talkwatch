using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class ReportEmailTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Password = "a long enough password";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value))));
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
    }

    private static async Task<T> DbAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    /// <summary>The app with calls, a reader granted one line, a person with no address, and a site email channel.</summary>
    private async Task<(WebApplicationFactory<Program> App, HttpClient Admin, Guid Reader, Guid NoEmail, Guid Channel)> StartAsync(FakeSmtpServer? smtp, string? username = null)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.ShiftTimes(DateTimeOffset.UtcNow.AddDays(-2));
        var app = talkwatch.Create(console, settings: smtp is null ? null : new Dictionary<string, string>
        {
            ["Smtp:Host"] = "127.0.0.1", ["Smtp:Port"] = smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Smtp:From"] = "talkwatch@example.test", ["Smtp:StartTls"] = "false", ["Smtp:Username"] = username ?? "", ["Smtp:Password"] = username is null ? "" : "secret",
        });
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "reader"), ("Role", Roles.Viewer), ("Password", Password));
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "noemail"), ("Role", Roles.Viewer), ("Password", Password));
        var (reader, noEmail, channel) = await DbAsync(app, async db =>
        {
            var people = await db.Users.Where(u => u.UserName == "reader" || u.UserName == "noemail").ToDictionaryAsync(u => u.UserName!, Ct);
            people["reader"].Email = "reader@example.test";
            var office = new AlertChannel { Id = Guid.NewGuid(), SiteId = people["reader"].SiteId, Name = "Office mail", Kind = ChannelKind.Email, Target = "office@example.test", CreatedAt = DateTimeOffset.UtcNow };
            db.AlertChannels.Add(office);
            await db.SaveChangesAsync(Ct);
            return (people["reader"].Id, people["noemail"].Id, office.Id);
        });
        var line = await DbAsync(app, db => db.CallLines.GroupBy(l => new { l.Kind, l.Key }).OrderBy(g => g.Count()).Select(g => g.Key).FirstAsync(Ct));
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/grants", ("Line", $"{line.Kind}:{line.Key}"));
        return (app, admin, reader, noEmail, channel);
    }

    private static (string, string)[] Report(params (string, string)[] recipients) =>
    [
        ("Name", "Weekly calls"), ("Schedule", "Cron"), ("Cron", "0 7 * * 1"), ("Period", "Days"), ("PeriodDays", "92"), ("Enabled", "true"),
        ("Sections", "Figures"), ("Sections", "CallList"), .. recipients,
    ];

    [Fact]
    public async Task Each_recipient_is_emailed_their_own_copy_as_html_with_the_call_list_and_a_person_with_no_address_is_recorded()
    {
        await using var smtp = new FakeSmtpServer();
        var (app, admin, reader, noEmail, channel) = await StartAsync(smtp);
        await using var _ = app;
        using var __ = admin;
        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Users", reader.ToString()), ("Users", noEmail.ToString()), ("Channels", channel.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report}/run");

        var mail = smtp.Messages.ToDictionary(m => Assert.Single(m.To));
        Assert.Equal(["office@example.test", "reader@example.test"], mail.Keys.Order(StringComparer.Ordinal));
        Assert.All(mail.Values, m =>
        {
            Assert.Contains("Subject: Weekly calls:", m.Data, StringComparison.Ordinal);
            Assert.Contains("Content-Type: text/html", m.Data, StringComparison.Ordinal);
            Assert.Contains("Content-Type: text/plain", m.Data, StringComparison.Ordinal);
            Assert.Matches("filename=\"?calls-", m.Data);
            // From TalkWatch by name, and in TalkWatch's own layout with the site's name in its header.
            Assert.Matches("From: \"?TalkWatch\"? <", m.Data);
        });
        var copies = await DbAsync(app, db => db.ReportRuns.Select(r => r.Html).ToListAsync(Ct));
        Assert.All(copies, html => Assert.Contains("data-report-site", html, StringComparison.Ordinal));

        // The reader's copy is theirs; the site channel's is the whole site's. Which copy each went with:
        var deliveries = await DbAsync(app, db => db.ReportDeliveries.Join(db.ReportRuns, d => d.RunId, r => r.Id, (d, r) => new { d.Address, d.State, d.LastError, r.AudienceUserId, r.Id }).ToListAsync(Ct));
        Assert.Equal(reader, deliveries.Single(d => d.Address == "reader@example.test").AudienceUserId);
        Assert.Null(deliveries.Single(d => d.Address == "office@example.test").AudienceUserId);
        var none = deliveries.Single(d => d.Address.Length == 0);
        Assert.Equal((DeliveryState.Dead, "No email address on their account."), (none.State, none.LastError));
        var page = await admin.GetStringAsync(new Uri($"/reports/runs/{none.Id}", UriKind.Relative), Ct);
        Assert.Contains("not sent: No email address on their account.", WebUtility.HtmlDecode(page), StringComparison.Ordinal);
    }

    // A username given to a server that offers no sign-in (most offer it only over TLS) threw past the mailer and stopped
    // TalkWatch; the copy stayed due, so it stopped again at every start.
    [Fact]
    public async Task A_server_offering_no_sign_in_leaves_the_copy_waiting_and_saying_why_and_talkwatch_running()
    {
        await using var smtp = new FakeSmtpServer();
        var (app, admin, _, _, channel) = await StartAsync(smtp, username: "talkwatch@example.test");
        await using var __ = app;
        using var ___ = admin;
        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Channels", channel.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report}/run");
        await app.Services.GetRequiredService<ReportMailer>().SendDueAsync(Ct);

        var delivery = await DbAsync(app, db => db.ReportDeliveries.SingleAsync(Ct));
        Assert.Equal((DeliveryState.Pending, 1), (delivery.State, delivery.Attempts));
        Assert.Contains("only over an encrypted connection: turn STARTTLS on", delivery.LastError, StringComparison.Ordinal);
        Assert.Empty(smtp.Messages);
    }

    // A server that offers sign-in without TLS would be sent the password in clear: TalkWatch doesn't send it.
    [Fact]
    public async Task The_mail_servers_password_is_never_sent_unencrypted_even_to_a_server_that_asks_for_it()
    {
        await using var smtp = new FakeSmtpServer(offersSignIn: true);
        var (app, admin, _, _, channel) = await StartAsync(smtp, username: "talkwatch@example.test");
        await using var __ = app;
        using var ___ = admin;
        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Channels", channel.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report}/run");
        await app.Services.GetRequiredService<ReportMailer>().SendDueAsync(Ct);

        var delivery = await DbAsync(app, db => db.ReportDeliveries.SingleAsync(Ct));
        Assert.Empty(smtp.SignIns);
        Assert.Empty(smtp.Messages);
        Assert.Contains("only over an encrypted connection: turn STARTTLS on", delivery.LastError, StringComparison.Ordinal);
    }

    // A person's page shows where their reports go and how they sign in, and the address can be put right there.
    [Fact]
    public async Task A_persons_page_shows_their_email_and_how_they_sign_in_and_takes_a_corrected_one()
    {
        var (app, admin, reader, _, _) = await StartAsync(smtp: null);
        await using var __ = app;
        using var ___ = admin;

        var page = await admin.GetStringAsync(new Uri($"/admin/users/{reader}", UriKind.Relative), Ct);
        Assert.Contains("data-email=\"reader@example.test\"", page, StringComparison.Ordinal);
        Assert.Contains("Signs in with a TalkWatch password.", page, StringComparison.Ordinal);

        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/email", ("Email", "reader.new@example.test"));
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/email", ("Email", "not an address"));

        Assert.Equal("reader.new@example.test", await DbAsync(app, db => db.Users.Where(u => u.Id == reader).Select(u => u.Email).SingleAsync(Ct)));
        Assert.Equal(1, await DbAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "user.email", Ct)));
    }

    // A copy for a person waits for its next try with the address it ran with; corrected since, the new one is used.
    [Fact]
    public async Task A_persons_copy_goes_to_their_email_as_it_is_at_each_try()
    {
        var (app, admin, reader, _, _) = await StartAsync(smtp: null);
        await using var __ = app;
        using var ___ = admin;
        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Users", reader.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        await PostAsync(admin, "/reports", $"/reports/{report}/run");
        Assert.Equal("reader@example.test", await DbAsync(app, db => db.ReportDeliveries.Select(d => d.Address).SingleAsync(Ct)));

        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/email", ("Email", "reader.new@example.test"));
        await DbAsync(app, async db => { var d = await db.ReportDeliveries.SingleAsync(Ct); d.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); return await db.SaveChangesAsync(Ct); });
        await app.Services.GetRequiredService<ReportMailer>().SendDueAsync(Ct);

        var delivery = await DbAsync(app, db => db.ReportDeliveries.SingleAsync(Ct));
        Assert.Equal(("reader.new@example.test", 2), (delivery.Address, delivery.Attempts));
    }

    // A reporting address takes a person's reports instead of their email; emptied, their email has them again.
    [Fact]
    public async Task Reports_go_to_a_persons_reporting_address_and_to_their_email_when_it_is_empty()
    {
        var (app, admin, reader, _, _) = await StartAsync(smtp: null);
        await using var __ = app;
        using var ___ = admin;

        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/report-email", ("ReportEmail", "not an address"));
        Assert.Null(await DbAsync(app, db => db.Users.Where(u => u.Id == reader).Select(u => u.ReportEmail).SingleAsync(Ct)));
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/report-email", ("ReportEmail", " reports@example.test "));
        Assert.Contains("data-report-email=\"reports@example.test\"", await admin.GetStringAsync(new Uri($"/admin/users/{reader}", UriKind.Relative), Ct), StringComparison.Ordinal);

        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Users", reader.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));
        await PostAsync(admin, "/reports", $"/reports/{report}/run");
        Assert.Equal("reports@example.test", await DbAsync(app, db => db.ReportDeliveries.Select(d => d.Address).SingleAsync(Ct)));

        // Emptied: the next try goes to their email, as it would have from the start.
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/report-email", ("ReportEmail", ""));
        await DbAsync(app, async db => { var d = await db.ReportDeliveries.SingleAsync(Ct); d.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); return await db.SaveChangesAsync(Ct); });
        await app.Services.GetRequiredService<ReportMailer>().SendDueAsync(Ct);
        Assert.Equal("reader@example.test", await DbAsync(app, db => db.ReportDeliveries.Select(d => d.Address).SingleAsync(Ct)));
        Assert.Equal(2, await DbAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "user.report_email", Ct)));
    }

    [Fact]
    public async Task Someone_with_only_a_reporting_address_still_gets_their_report()
    {
        var (app, admin, _, noEmail, _) = await StartAsync(smtp: null);
        await using var __ = app;
        using var ___ = admin;
        await PostAsync(admin, $"/admin/users/{noEmail}", $"/admin/users/{noEmail}/report-email", ("ReportEmail", "team@example.test"));
        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Users", noEmail.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report}/run");

        var delivery = await DbAsync(app, db => db.ReportDeliveries.SingleAsync(Ct));
        Assert.Equal(("team@example.test", DeliveryState.Pending), (delivery.Address, delivery.State));
    }

    [Fact]
    public async Task With_no_mail_server_a_copy_waits_and_says_why_rather_than_being_lost()
    {
        var (app, admin, _, _, channel) = await StartAsync(smtp: null);
        await using var __ = app;
        using var ___ = admin;
        await PostAsync(admin, "/reports/new", "/reports/save", Report(("Channels", channel.ToString())));
        var report = await DbAsync(app, db => db.Reports.Select(r => r.Id).SingleAsync(Ct));

        await PostAsync(admin, "/reports", $"/reports/{report}/run");

        var delivery = await DbAsync(app, db => db.ReportDeliveries.SingleAsync(Ct));
        Assert.Equal((DeliveryState.Pending, 1), (delivery.State, delivery.Attempts));
        Assert.Contains("Email is not set up", delivery.LastError, StringComparison.Ordinal);
        Assert.True(delivery.NextAttemptAt > DateTimeOffset.UtcNow.AddMinutes(4));
        // The copy's page says how many tries it has had and when the next is, so a wait does not look stuck.
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/reports/runs/{delivery.RunId}", UriKind.Relative), Ct));
        var zone = app.Services.GetRequiredService<ReportBuilder>().Zone;
        // Today's next try reads as a time; one that falls after midnight, as it does five minutes before, carries its day.
        var next = TimeZoneInfo.ConvertTime(delivery.NextAttemptAt, zone);
        var when = next.Date == TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date ? $"at {next:HH:mm}" : $"{next:ddd HH:mm}";
        Assert.Contains($"tried 1 of {ReportMailer.MaxAttempts} times; tries again {when}: Email is not set up", page, StringComparison.Ordinal);
    }
}

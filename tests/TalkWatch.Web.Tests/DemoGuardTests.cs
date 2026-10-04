using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// The demo guest is an admin who may try everything, but not change what would change the site for every visitor after
/// them, and nothing in a demo is sent out of the browser.
/// </summary>
public sealed partial class DemoGuardTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The demo sets the guest up itself as it starts; waited for rather than set up again beside it.
    private static async Task<(WebApplicationFactory<Program> App, Guid Guest)> DemoAsync(TalkWatchApp talkwatch)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), fakeConsole: false,
            settings: new Dictionary<string, string> { ["Demo:Enabled"] = "true", ["Demo:Fixtures"] = FixtureConsole.DefaultDirectory, ["Demo:CallEveryMinutes"] = "0" });
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(60); waited += TimeSpan.FromMilliseconds(250))
        {
            using var scope = app.Services.CreateScope();
            if (await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().FindByNameAsync("guest") is { } guest
                && await scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>().IsInRoleAsync(guest, DemoActivity.GuestRole))
            {
                return (app, guest.Id);
            }

            await Task.Delay(250, Ct);
        }

        throw new TimeoutException("The demo did not set its guest up.");
    }

    private static Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>()).ContinueWith(t => { scope.Dispose(); return t.Result; }, Ct, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string token, string path, string from, params (string Key, string Value)[] fields)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative))
        {
            Content = new FormUrlEncodedContent([new("__RequestVerificationToken", token), .. fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value))]),
        };
        request.Headers.Referrer = new Uri(browser.BaseAddress!, from);
        return await browser.SendAsync(request, Ct);
    }

    [Fact]
    public async Task The_guest_sees_the_admin_pages_and_tries_browser_alerts_but_cannot_change_people_settings_or_send_out()
    {
        var (app, guest) = await DemoAsync(talkwatch);
        await using var _ = app;
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "guest", new DemoOptions().GuestPassword);

        var people = await browser.GetAsync(new Uri("/admin/users", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, people.StatusCode);
        var token = WebUtility.HtmlDecode(Token().Match(await people.Content.ReadAsStringAsync(Ct)).Groups[1].Value);

        var addPerson = await PostAsync(browser, token, "/admin/users", "/admin/users", ("Username", "intruder"), ("Role", Roles.Admin), ("Password", "a long enough password"));
        Assert.StartsWith("/admin/users?msg=", addPerson.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains(Uri.EscapeDataString(DemoGuard.Refusal), addPerson.Headers.Location.OriginalString, StringComparison.Ordinal);
        Assert.False(await AsSystemAsync(app, db => db.Users.AnyAsync(u => u.UserName == "intruder", Ct)));

        await PostAsync(browser, token, "/admin/alerts/settings", "/admin/alerts", ("SmtpHost", "smtp.example.invalid"));
        var test = await PostAsync(browser, token, "/admin/alerts/settings/test", "/admin/alerts", ("To", "someone@example.invalid"));
        await PostAsync(browser, token, "/admin/alerts/answering-lines", "/admin/alerts", ("ContactId", "3"), ("Ticked", "true"), ("Phrases", "is not available"));
        Assert.False(await AsSystemAsync(app, db => db.AnsweringLines.AnyAsync(Ct)));
        Assert.Contains(Uri.EscapeDataString("The demo sends no email"), test.Headers.Location!.OriginalString, StringComparison.Ordinal);
        await PostAsync(browser, token, "/admin/alerts/channels", "/admin/alerts", ("Name", "Out"), ("Kind", nameof(ChannelKind.Webhook)), ("Target", "https://example.invalid/hook"));
        await PostAsync(browser, token, "/admin/alerts/channels", "/admin/alerts", ("Name", "Mine"), ("Kind", nameof(ChannelKind.Browser)), ("Target", ""), ("Owner", guest.ToString()));

        var channels = await AsSystemAsync(app, db => db.AlertChannels.Select(c => new { c.Name, c.Kind }).ToListAsync(Ct));
        Assert.DoesNotContain(channels, c => c.Kind == ChannelKind.Webhook);
        Assert.Contains(channels, c => c.Name == "Mine" && c.Kind == ChannelKind.Browser);
    }

    [Fact]
    public async Task In_a_demo_an_alert_for_anything_but_the_browser_is_cancelled_saying_why_and_never_sent()
    {
        var (app, _) = await DemoAsync(talkwatch);
        await using var __ = app;
        var now = DateTimeOffset.UtcNow;
        var delivery = await AsSystemAsync(app, async db =>
        {
            var site = await db.Sites.Select(s => s.Id).SingleAsync(Ct);
            var flow = new AlertFlow { Id = Guid.NewGuid(), SiteId = site, Name = "Out", Trigger = AlertEventType.MissedCall, Definition = "{}", CreatedAt = now };
            var channel = new AlertChannel { Id = Guid.NewGuid(), SiteId = site, Name = "Hook", Kind = ChannelKind.Webhook, Target = "https://example.invalid/hook", CreatedAt = now };
            var alert = new AlertEvent { Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.MissedCall, Key = "k", At = now, Title = "Missed", Message = "A call was missed" };
            var pending = new AlertDelivery { Id = Guid.NewGuid(), SiteId = site, EventId = alert.Id, FlowId = flow.Id, ChannelId = channel.Id, State = DeliveryState.Pending, NextAttemptAt = now, CreatedAt = now };
            db.AddRange(flow, channel, alert, pending);
            await db.SaveChangesAsync(Ct);
            return pending.Id;
        });

        await app.Services.GetRequiredService<AlertDispatcher>().RunOnceAsync(Ct);

        var after = await AsSystemAsync(app, db => db.AlertDeliveries.SingleAsync(d => d.Id == delivery, Ct));
        Assert.Equal(DeliveryState.Cancelled, after.State);
        Assert.Equal(AlertDispatcher.InBrowserOnly, after.LastError);
        Assert.Equal(0, after.Attempts);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();
}

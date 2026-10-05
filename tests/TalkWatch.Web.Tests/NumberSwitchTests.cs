using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The rail's number switcher: one DID, or all numbers, kept on the account and followed by every call page.</summary>
public sealed partial class NumberSwitchTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Did = "+441144960042";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WebApplicationFactory<Program>> PolledAsync(TalkWatchApp talkwatch)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        return app;
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    // Chooses through the switcher's own form on a page, antiforgery token and all.
    private static async Task<HttpResponseMessage> ChooseAsync(HttpClient browser, string did, string from = "/calls")
    {
        var page = await browser.GetStringAsync(new Uri(from, UriKind.Relative), Ct);
        var form = SwitchForm().Match(page);
        Assert.True(form.Success, "no number switcher on " + from);
        using var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(form.Value).Groups[1].Value)),
            new("ReturnUrl", from), new("Did", did)]);
        return await browser.PostAsync(new Uri("/account/number", UriKind.Relative), content, Ct);
    }

    [Fact]
    public async Task Choosing_a_number_narrows_the_calls_to_it_on_every_device_until_all_numbers_is_chosen_again()
    {
        await using var app = await PolledAsync(talkwatch);
        var all = await AsSystemAsync(app, db => db.Calls.CountAsync(Ct));
        var onIt = await AsSystemAsync(app, db => db.CallLines.CountAsync(l => l.Kind == LineKind.Did && l.Key == Did, Ct));
        Assert.InRange(onIt, 1, all - 1);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var before = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct));
        Assert.Contains($"of {all}", before, StringComparison.Ordinal);
        Assert.DoesNotContain("data-chosen", before, StringComparison.Ordinal);

        var chosen = await ChooseAsync(browser, Did);

        Assert.Equal(HttpStatusCode.Redirect, chosen.StatusCode);
        Assert.Equal("/calls", chosen.Headers.Location!.OriginalString);
        var narrowed = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct));
        Assert.Contains($"of {onIt}", narrowed, StringComparison.Ordinal);
        Assert.Contains("data-chosen", narrowed, StringComparison.Ordinal);

        // Another device: the same person signing in elsewhere sees the same number.
        using var phone = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(phone, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        Assert.Contains($"of {onIt}", WebUtility.HtmlDecode(await phone.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)), StringComparison.Ordinal);

        await ChooseAsync(browser, "");
        Assert.Contains($"of {all}", WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_someone_cannot_see_is_refused_and_with_one_number_there_is_nothing_to_switch()
    {
        await using var app = await PolledAsync(talkwatch);
        var other = await AsSystemAsync(app, db => db.Lines.Where(l => l.Kind == LineKind.Did && l.Key != Did).Select(l => l.Key).FirstAsync(Ct));
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = scope.ServiceProvider.GetRequiredService<CurrentSite>().Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = viewer.SiteId, UserId = viewer.Id, Kind = LineKind.Did, Key = Did, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "viewer", "a long enough password");
        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        Assert.DoesNotContain("data-number-switch", page, StringComparison.Ordinal);

        // Posted by hand, with the sign-out form's token: the number they have no grant on is refused, and kept off the account.
        using var content = new FormUrlEncodedContent([
            new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value)), new("ReturnUrl", "/calls"), new("Did", other)]);
        var refused = await browser.PostAsync(new Uri("/account/number", UriKind.Relative), content, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Null(await AsSystemAsync(app, db => db.Users.Where(u => u.UserName == "viewer").Select(u => u.ContextDid).SingleAsync(Ct)));
    }

    // Rang a number and nobody answered, a moment ago.
    private static string MissedJustNow(string uuid, string to)
    {
        var time = DateTimeOffset.UtcNow.AddSeconds(-40).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            {"uuid":"{{uuid}}","time":"{{time}}","direction":"in","status":"accepted","duration":30,"from":"+447700900318","to":"{{to}}",
             "call_events":[{"time":"{{time}}","event":"call_started"},{"time":"{{time}}","event":"seq_call_trying_endpoints"},{"time":"{{time}}","event":"call_hangup"}]}
            """;
    }

    [Fact]
    public async Task Now_follows_the_chosen_number_its_calls_and_how_busy_its_streams_are()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var other = await AsSystemAsync(app, db => db.Lines.Where(l => l.Kind == LineKind.Did && l.Key != Did).Select(l => l.Key).FirstAsync(Ct));
        string mine = Guid.NewGuid().ToString(), theirs = Guid.NewGuid().ToString();
        console.AddCall(MissedJustNow(mine, Did));
        console.AddCall(MissedJustNow(theirs, other));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        await ChooseAsync(browser, Did, "/live");
        var raw = await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct);

        Assert.Contains($"data-live-call=\"{mine}\"", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(theirs, raw, StringComparison.Ordinal);
        var rates = System.Text.Json.JsonDocument.Parse(WebUtility.HtmlDecode(Regex.Match(raw, "data-rates=\"([^\"]*)\"").Groups[1].Value)).RootElement;
        Assert.Equal(0.2, rates.GetProperty("missed").GetDouble());
        Assert.Equal(0.2, rates.GetProperty("inlet").GetDouble());
    }

    [GeneratedRegex(@"<form[^>]*action=""/account/number""[\s\S]*?</form>")]
    private static partial Regex SwitchForm();

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();
}

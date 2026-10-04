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

/// <summary>
/// A number as an access boundary: a role held on one number opens that number's calls (on the DID, and through the
/// switchboard and ring groups it routes to), and what the role allows applies to those calls and no others.
/// </summary>
public sealed partial class NumberRoleTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    // The second number routes through switchboard 15 to ring group 1; the main line through switchboard 45.
    private const string Support = "+441174960404";
    private const string Main = "+441144960042";
    private const string Password = "a long enough password";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string VoicemailOnSupport(string uuid) => $$$"""
        {"uuid": "{{{uuid}}}", "time": "2026-09-30T16:57:17.85Z", "direction": "in", "status": "accepted", "duration": 60,
         "from": "+447700900774", "to": "{{{Support}}}", "to_smart_attendant_id": "15",
         "call_events": [
           {"time": "2026-09-30T16:57:17.898Z", "event": "call_started", "event_data": {"to_smart_attendant_id": 15}},
           {"time": "2026-09-30T16:57:57.895Z", "event": "call_sent_to_voicemail", "event_data": {"recipient_user_uuids": ["abe3a229-7539-48dd-a8e3-e23fd4d9f72f"]}},
           {"time": "2026-09-30T16:58:18.224Z", "event": "vm_msg_recorded", "event_data": {"recipient_user_uuids": ["abe3a229-7539-48dd-a8e3-e23fd4d9f72f"]}},
           {"time": "2026-09-30T16:58:18.313Z", "event": "call_hangup", "event_data": {"hangup_cause": "normal_end"}}
         ]}
        """;

    private static async Task<WebApplicationFactory<Program>> ReadyAsync(TalkWatchApp talkwatch, FixtureConsole? console = null)
    {
        var app = talkwatch.Create(console ?? new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        return app;
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, IServiceProvider, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider);
    }

    /// <summary>A role with exactly these permissions; its name says what it is for.</summary>
    private static Task<Guid> RoleAsync(WebApplicationFactory<Program> app, string name, Permission permissions) => AsSystemAsync(app, async (_, sp) =>
    {
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var role = new IdentityRole<Guid>(name);
        await roles.CreateAsync(role);
        await RolePermissions.ChangeAsync(roles, role, permissions);
        return role.Id;
    });

    /// <summary>Someone whose site role allows nothing and who is granted no lines: only roles on numbers give them anything.</summary>
    private static Task<Guid> PersonAsync(WebApplicationFactory<Program> app, string name) => AsSystemAsync(app, async (_, sp) =>
    {
        var users = sp.GetRequiredService<UserManager<AppUser>>();
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (await roles.FindByNameAsync("Nothing site-wide") is null)
        {
            var none = new IdentityRole<Guid>("Nothing site-wide");
            await roles.CreateAsync(none);
            await RolePermissions.ChangeAsync(roles, none, Permission.None);
        }

        var person = new AppUser { Id = Guid.NewGuid(), UserName = name, SiteId = sp.GetRequiredService<CurrentSite>().Id };
        Assert.True((await users.CreateAsync(person, Password)).Succeeded);
        await users.AddToRoleAsync(person, "Nothing site-wide");
        return person.Id;
    });

    private static Task<int> HoldAsync(WebApplicationFactory<Program> app, Guid person, string did, Guid role) => AsSystemAsync(app, async (db, sp) =>
    {
        db.NumberRoles.Add(new NumberRole { Id = Guid.NewGuid(), SiteId = sp.GetRequiredService<CurrentSite>().Id, UserId = person, Did = did, RoleId = role, CreatedAt = DateTimeOffset.UtcNow });
        return await db.SaveChangesAsync(Ct);
    });

    /// <summary>The calls a number covers, worked out from the call lines and the console's routing directly.</summary>
    private static Task<List<string>> CoveredAsync(WebApplicationFactory<Program> app, string did) => AsSystemAsync(app, async (db, _) =>
    {
        var routes = (await db.NumberRoutes.Where(r => r.Did == did).ToListAsync(Ct)).Select(r => (r.Kind, r.Key)).ToHashSet();
        routes.Add((LineKind.Did, did));
        var lines = await db.CallLines.Select(l => new { l.CallId, l.Kind, l.Key }).ToListAsync(Ct);
        var ids = lines.Where(l => routes.Contains((l.Kind, l.Key))).Select(l => l.CallId).ToHashSet();
        return await db.Calls.Where(c => ids.Contains(c.Id)).Select(c => c.TalkUuid).ToListAsync(Ct);
    });

    private static async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> app, string name)
    {
        var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, name, Password);
        return browser;
    }

    // The call log's heading, "47 calls"; with none, the page shows its empty state instead.
    private static int Total(string page)
    {
        Assert.Contains("<title>Calls", page, StringComparison.Ordinal);
        return CallsHeading().Match(page) is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.AllowThousands, System.Globalization.CultureInfo.InvariantCulture) : 0;
    }

    [Fact]
    public async Task A_role_on_one_number_shows_that_numbers_calls_and_what_it_routes_to_and_none_of_another_numbers()
    {
        await using var app = await ReadyAsync(talkwatch);
        var supportCalls = await CoveredAsync(app, Support);
        var everything = await AsSystemAsync(app, (db, _) => db.Calls.CountAsync(Ct));
        Assert.InRange(supportCalls.Count, 1, everything - 1);
        // The routing was read: the number covers its switchboard and the ring group its menus reach, not only itself.
        Assert.True(await AsSystemAsync(app, (db, _) => db.NumberRoutes.AnyAsync(r => r.Did == Support && r.Kind == LineKind.RingGroup && r.Key == "1", Ct)));

        var viewer = await RoleAsync(app, "Number viewer", Permission.None);
        var sam = await PersonAsync(app, "sam");
        using var browser = await SignedInAsync(app, "sam");
        Assert.Equal(0, Total(await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)));

        await HoldAsync(app, sam, Support, viewer);

        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        Assert.Equal(supportCalls.Count, Total(page));
        var other = (await AsSystemAsync(app, (db, _) => db.Calls.Select(c => c.TalkUuid).ToListAsync(Ct))).Except(supportCalls).First();
        const string Unseen = "No such call, or not one you can see.";
        Assert.Contains(Unseen, await browser.GetStringAsync(new Uri($"/calls/{other}", UriKind.Relative), Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(Unseen, await browser.GetStringAsync(new Uri($"/calls/{supportCalls[0]}", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hearing_a_voicemail_needs_a_role_allowing_audio_on_a_number_that_covers_the_call()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.AddCall(VoicemailOnSupport("ce248ac4-a417-4b2b-8d8f-4013b1518a61"));
        await using var app = await ReadyAsync(talkwatch, console);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        var voicemail = await AsSystemAsync(app, (db, _) => db.AudioFiles.SingleAsync(a => a.Kind == AudioKind.Voicemail, Ct));

        var viewer = await RoleAsync(app, "Number viewer", Permission.None);
        var listener = await RoleAsync(app, "Number listener", Permission.AllAudio);
        await HoldAsync(app, await PersonAsync(app, "sees"), Support, viewer);
        await HoldAsync(app, await PersonAsync(app, "hears"), Support, listener);
        await HoldAsync(app, await PersonAsync(app, "elsewhere"), Main, listener);

        using var sees = await SignedInAsync(app, "sees");
        using var hears = await SignedInAsync(app, "hears");
        using var elsewhere = await SignedInAsync(app, "elsewhere");
        var audio = new Uri($"/audio/{voicemail.Id}", UriKind.Relative);
        Assert.Equal(HttpStatusCode.OK, (await hears.GetAsync(audio, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sees.GetAsync(audio, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await elsewhere.GetAsync(audio, Ct)).StatusCode);
    }

    [Fact]
    public async Task Export_on_a_number_exports_that_numbers_calls_and_without_it_anywhere_there_is_none()
    {
        await using var app = await ReadyAsync(talkwatch);
        var exporter = await RoleAsync(app, "Number exporter", Permission.Export);
        var viewer = await RoleAsync(app, "Number viewer", Permission.None);
        await HoldAsync(app, await PersonAsync(app, "exports"), Support, exporter);
        await HoldAsync(app, await PersonAsync(app, "looks"), Support, viewer);
        var times = await AsSystemAsync(app, (db, _) => db.Calls.Select(c => c.Time).ToListAsync(Ct));
        var (from, to) = (DateOnly.FromDateTime(times.Min().UtcDateTime.AddDays(-1)), DateOnly.FromDateTime(times.Max().UtcDateTime.AddDays(1)));
        var path = new Uri($"/calls/export.csv?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", UriKind.Relative);

        using var exports = await SignedInAsync(app, "exports");
        using var looks = await SignedInAsync(app, "looks");
        var csv = await exports.GetStringAsync(path, Ct);
        var supportCalls = await CoveredAsync(app, Support);

        Assert.Equal(supportCalls.Count, csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length - 1);
        Assert.All(supportCalls, uuid => Assert.Contains(uuid, csv, StringComparison.Ordinal));
        // Refused: with cookie sign-in that is a redirect to the sign-in page, never the file.
        var refused = await looks.GetAsync(path, Ct);
        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        Assert.Contains("/signin", refused.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_number_admin_gives_roles_within_their_own_on_their_number_only_and_taking_one_away_works_at_once()
    {
        await using var app = await ReadyAsync(talkwatch);
        var admin = await RoleAsync(app, "Number admin", Permission.ManageNumberPeople | Permission.Export);
        var viewer = await RoleAsync(app, "Number viewer", Permission.None);
        var listener = await RoleAsync(app, "Number listener", Permission.AllAudio);
        await HoldAsync(app, await PersonAsync(app, "lead"), Support, admin);
        var newcomer = await PersonAsync(app, "newcomer");
        using var lead = await SignedInAsync(app, "lead");
        using var newcomerBrowser = await SignedInAsync(app, "newcomer");

        var page = await lead.GetStringAsync(new Uri("/numbers", UriKind.Relative), Ct);
        var shown = WebUtility.HtmlDecode(page);
        Assert.Contains($"data-number=\"{Support}\"", shown, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-number=\"{Main}\"", shown, StringComparison.Ordinal);
        var token = WebUtility.HtmlDecode(Token().Match(page).Groups[1].Value);
        async Task<HttpResponseMessage> GiveAsync(string did, Guid role)
        {
            using var form = new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Did", did), new("PersonId", newcomer.ToString()), new("RoleId", role.ToString())]);
            return await lead.PostAsync(new Uri("/numbers/people", UriKind.Relative), form, Ct);
        }

        // Beyond their own: they cannot hear audio on the number, so they cannot let anyone else.
        await GiveAsync(Support, listener);
        // Not their number.
        Assert.Contains("/signin", (await GiveAsync(Main, viewer)).Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
        Assert.False(await AsSystemAsync(app, (db, _) => db.NumberRoles.AnyAsync(n => n.UserId == newcomer, Ct)));

        await GiveAsync(Support, viewer);
        var held = await AsSystemAsync(app, (db, _) => db.NumberRoles.SingleAsync(n => n.UserId == newcomer, Ct));
        Assert.Equal((Support, viewer), (held.Did, held.RoleId));
        Assert.True(Total(await newcomerBrowser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)) > 0);

        using (var form = new FormUrlEncodedContent([new("__RequestVerificationToken", token), new("Id", held.Id.ToString())]))
        {
            await lead.PostAsync(new Uri("/numbers/people/remove", UriKind.Relative), form, Ct);
        }

        // No waiting for a cookie to refresh: the next page already shows nothing.
        Assert.Equal(0, Total(await newcomerBrowser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)));
    }

    [GeneratedRegex(@"<h2>([0-9,]+) calls?</h2>")]
    private static partial Regex CallsHeading();

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();
}

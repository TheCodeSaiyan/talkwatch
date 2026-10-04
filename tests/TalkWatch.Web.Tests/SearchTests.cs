using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The command palette's search, and the call log's line filter it leads to: both through the viewer's scope.</summary>
public sealed class SearchTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string Did = "+441144960042";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<WebApplicationFactory<Program>> PolledAsync(TalkWatchApp talkwatch)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        return app;
    }

    private static async Task<CallRow> AnyCallFromAMobileAsync(WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.AsNoTracking()
            .Where(c => c.Direction == "in" && c.FromE164 != null && c.FromE164.StartsWith("+447")).OrderByDescending(c => c.Time).FirstAsync(Ct);
    }

    private static async Task ViewerAsync(WebApplicationFactory<Program> app, string name, Grant? grant)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var viewer = new AppUser { Id = Guid.NewGuid(), UserName = name, SiteId = site.Id };
        Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
        await users.AddToRoleAsync(viewer, Roles.Viewer);
        if (grant is not null)
        {
            grant.Id = Guid.NewGuid();
            grant.SiteId = site.Id;
            grant.UserId = viewer.Id;
            grant.CreatedAt = DateTimeOffset.UtcNow;
            db.Grants.Add(grant);
            await db.SaveChangesAsync(Ct);
        }
    }

    [Fact]
    public async Task Part_of_a_number_typed_the_way_people_write_it_finds_the_calls_from_it()
    {
        await using var app = await PolledAsync(talkwatch);
        var call = await AnyCallFromAMobileAsync(app);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        // "+447700900083" is written "07700 900083" in the UK: the leading zero and the space must not matter.
        var national = "0" + call.FromE164![3..7] + " " + call.FromE164[7..];
        var result = await admin.GetFromJsonAsync<SearchEndpoints.SearchResult>($"/search?q={Uri.EscapeDataString(national)}", Ct);

        Assert.NotNull(result);
        Assert.Contains(result.Calls, c => c.Uuid == call.TalkUuid);
    }

    [Fact]
    public async Task Someone_with_no_lines_finds_no_calls_and_no_lines()
    {
        await using var app = await PolledAsync(talkwatch);
        var call = await AnyCallFromAMobileAsync(app);
        await ViewerAsync(app, "nobody", grant: null);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "nobody", "a long enough password");

        var byNumber = await browser.GetFromJsonAsync<SearchEndpoints.SearchResult>($"/search?q={Uri.EscapeDataString(call.FromE164![^6..])}", Ct);
        var byLine = await browser.GetFromJsonAsync<SearchEndpoints.SearchResult>($"/search?q={Uri.EscapeDataString(Did[^6..])}", Ct);

        Assert.Empty(byNumber!.Calls);
        Assert.Empty(byLine!.Lines);

        // ...and not because there was nothing to find: an administrator finds both.
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        Assert.NotEmpty((await admin.GetFromJsonAsync<SearchEndpoints.SearchResult>($"/search?q={Uri.EscapeDataString(call.FromE164[^6..])}", Ct))!.Calls);
        Assert.NotEmpty((await admin.GetFromJsonAsync<SearchEndpoints.SearchResult>($"/search?q={Uri.EscapeDataString(Did[^6..])}", Ct))!.Lines);
    }

    [Fact]
    public async Task Signed_out_the_search_answers_nothing()
    {
        await using var app = await PolledAsync(talkwatch);
        using var browser = TalkWatchApp.Browser(app);

        var response = await browser.GetAsync(new Uri("/search?q=0083", UriKind.Relative), Ct);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_call_log_filtered_to_a_line_shows_only_calls_that_touched_it_and_names_the_filter()
    {
        await using var app = await PolledAsync(talkwatch);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        int touching;
        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            touching = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().CallLines.CountAsync(l => l.Kind == LineKind.Did && l.Key == Did, Ct);
        }

        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/calls?line={Uri.EscapeDataString("Did:" + Did)}", UriKind.Relative), Ct));

        Assert.True(touching > 0);
        Assert.Contains($"of {touching}", page, StringComparison.Ordinal);
        Assert.Contains("data-chip=\"line\"", page, StringComparison.Ordinal);
    }
}

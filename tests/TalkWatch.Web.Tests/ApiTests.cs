using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
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

public sealed partial class ApiTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Everything = "since=2025-01-01T00:00:00Z&until=2027-01-01T00:00:00Z&pageSize=500";

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    [GeneratedRegex(@"data-new-token>([^<]+)<")]
    private static partial Regex NewToken();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value)));
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, UserManager<AppUser>, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    private async Task<WebApplicationFactory<Program>> ImportedAsync()
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        return app;
    }

    /// <summary>A viewer granted the busiest number, signed in. Returns their browser and the number.</summary>
    private static async Task<(HttpClient Browser, string Did)> ViewerAsync(WebApplicationFactory<Program> app)
    {
        var did = await AsSystemAsync(app, (db, _) => db.CallLines.Where(l => l.Kind == LineKind.Did)
            .GroupBy(l => l.Key).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstAsync(Ct));
        await AsSystemAsync(app, async (db, users) =>
        {
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = await db.Sites.Select(s => s.Id).SingleAsync(Ct) };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = viewer.SiteId, UserId = viewer.Id, Kind = LineKind.Did, Key = did, CreatedAt = DateTimeOffset.UtcNow });
            return await db.SaveChangesAsync(Ct);
        });
        var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "viewer", "a long enough password");
        return (browser, did);
    }

    /// <summary>Makes a token on the account page, as a person would, and reads it off the page.</summary>
    private static async Task<string> MakeTokenAsync(HttpClient browser, string name = "script")
    {
        var response = await PostAsync(browser, "/account", "/account", ("_handler", "new-token"), ("Token.Name", name));
        return WebUtility.HtmlDecode(NewToken().Match(await response.Content.ReadAsStringAsync(Ct)).Groups[1].Value);
    }

    private static HttpClient Api(WebApplicationFactory<Program> app, string token)
    {
        var client = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task A_token_returns_only_the_calls_its_owner_is_granted()
    {
        await using var app = await ImportedAsync();
        var (browser, did) = await ViewerAsync(app);
        using var _ = browser;
        var token = await MakeTokenAsync(browser);
        using var api = Api(app, token);

        using var body = JsonDocument.Parse(await api.GetStringAsync(new Uri($"/api/v1/calls?{Everything}", UriKind.Relative), Ct));

        var granted = await AsSystemAsync(app, (db, _) => db.CallLines.Where(l => l.Kind == LineKind.Did && l.Key == did).Select(l => l.CallId).Distinct().CountAsync(Ct));
        var all = await AsSystemAsync(app, (db, _) => db.Calls.CountAsync(Ct));
        var calls = body.RootElement.GetProperty("calls").EnumerateArray().ToList();
        Assert.StartsWith(ApiTokens.Prefix, token, StringComparison.Ordinal);
        Assert.True(granted < all, "The viewer should be granted only some of the calls.");
        Assert.Equal(granted, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(granted, calls.Count);
        Assert.All(calls, c => Assert.Contains(c.GetProperty("lines").EnumerateArray(), l => l.GetProperty("key").GetString() == did));
        Assert.All(calls, c => Assert.Equal(1, c.GetProperty("lines").GetArrayLength()));
    }

    [Fact]
    public async Task A_call_outside_the_owners_grants_is_not_found_and_their_figures_are_theirs()
    {
        await using var app = await ImportedAsync();
        var (browser, did) = await ViewerAsync(app);
        using var _ = browser;
        using var api = Api(app, await MakeTokenAsync(browser));
        var elsewhere = await AsSystemAsync(app, (db, _) => db.Calls.Where(c => !c.Lines.Any(l => l.Kind == LineKind.Did && l.Key == did)).Select(c => c.TalkUuid).FirstAsync(Ct));

        var hidden = await api.GetAsync(new Uri($"/api/v1/calls/{elsewhere}", UriKind.Relative), Ct);
        using var lines = JsonDocument.Parse(await api.GetStringAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct));
        var stats = await api.GetAsync(new Uri("/api/v1/stats?from=2026-06-29&to=2026-09-28", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.All(lines.RootElement.EnumerateArray(), l => Assert.Equal(did, l.GetProperty("key").GetString()));
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
    }

    [Fact]
    public async Task The_number_chosen_in_the_switcher_does_not_narrow_the_owners_tokens()
    {
        await using var app = await ImportedAsync();
        var (did, elsewhere) = await AsSystemAsync(app, async (db, users) =>
        {
            var did = await db.CallLines.Where(l => l.Kind == LineKind.Did).Select(l => l.Key).FirstAsync(Ct);
            var admin = await users.FindByNameAsync(TalkWatchApp.AdminUsername);
            admin!.ContextDid = did;
            await users.UpdateAsync(admin);
            var elsewhere = await db.Calls.Where(c => !c.Lines.Any(l => l.Kind == LineKind.Did && l.Key == did)).Select(c => c.TalkUuid).FirstAsync(Ct);
            return (did, elsewhere);
        });
        var all = await AsSystemAsync(app, (db, _) => db.Calls.CountAsync(Ct));
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        using var api = Api(app, await MakeTokenAsync(browser));

        using var body = JsonDocument.Parse(await api.GetStringAsync(new Uri($"/api/v1/calls?{Everything}", UriKind.Relative), Ct));
        var other = await api.GetAsync(new Uri($"/api/v1/calls/{elsewhere}", UriKind.Relative), Ct);

        // What a script gets back cannot hang on which number its owner last looked at in the browser.
        Assert.Equal(all, body.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        Assert.Contains($"of {await AsSystemAsync(app, (db, _) => db.CallLines.Where(l => l.Kind == LineKind.Did && l.Key == did).Select(l => l.CallId).Distinct().CountAsync(Ct))}",
            WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_token_a_wrong_one_a_revoked_one_or_a_locked_owners_one_is_refused()
    {
        await using var app = await ImportedAsync();
        var (browser, _) = await ViewerAsync(app);
        using var __ = browser;
        var revoked = await MakeTokenAsync(browser, "old");
        var tokenId = await AsSystemAsync(app, (db, _) => db.ApiTokens.Where(t => t.Name == "old").Select(t => t.Id).SingleAsync(Ct));
        await PostAsync(browser, "/account", "/account", ("_handler", $"revoke-{tokenId:N}"));
        var kept = await MakeTokenAsync(browser, "kept");

        var cookieOnly = await browser.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct);
        using var wrong = Api(app, ApiTokens.NewToken());
        using var afterRevoke = Api(app, revoked);
        using var working = Api(app, kept);
        var beforeLock = await working.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct);
        await AsSystemAsync(app, async (_, users) => await users.SetLockoutEndDateAsync((await users.FindByNameAsync("viewer"))!, DateTimeOffset.MaxValue));
        var afterLock = await working.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await afterRevoke.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, beforeLock.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLock.StatusCode);
        Assert.NotNull(await AsSystemAsync(app, (db, _) => db.ApiTokens.Where(t => t.Name == "kept").Select(t => t.LastUsedAt).SingleAsync(Ct)));
        Assert.DoesNotContain(kept, await AsSystemAsync(app, (db, _) => db.ApiTokens.Select(t => t.Hash).FirstAsync(Ct)), StringComparison.Ordinal);
    }

    // Making tokens is a permission: taken from a role, its members' tokens stop working too, not just new ones.
    [Fact]
    public async Task A_token_stops_working_when_its_owners_role_no_longer_allows_tokens()
    {
        await using var app = await ImportedAsync();
        var (browser, _) = await ViewerAsync(app);
        using var __ = browser;
        using var api = Api(app, await MakeTokenAsync(browser));
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct)).StatusCode);

        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            await RolePermissions.ChangeAsync(roles, (await roles.FindByNameAsync(Roles.Viewer))!, Permission.Export | Permission.MarkCallBacks);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await api.GetAsync(new Uri("/api/v1/lines", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_csv_export_holds_the_signed_in_persons_calls_and_is_audited()
    {
        await using var app = await ImportedAsync();
        var (browser, did) = await ViewerAsync(app);
        using var _ = browser;

        var response = await browser.GetAsync(new Uri("/calls/export.csv?from=2025-01-01&to=2025-12-31", UriKind.Relative), Ct);
        var tooLong = await browser.GetAsync(new Uri("/calls/export.csv?from=2025-01-01&to=2026-12-31", UriKind.Relative), Ct);
        var bytes = await browser.GetByteArrayAsync(new Uri("/calls/export.csv?from=2025-12-01&to=2026-09-30", UriKind.Relative), Ct);
        var wide = System.Text.Encoding.UTF8.GetString(bytes);

        var granted = await AsSystemAsync(app, (db, _) => db.CallLines.Where(l => l.Kind == LineKind.Did && l.Key == did).Select(l => l.CallId).Distinct().CountAsync(Ct));
        var rows = wide.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        // A byte order mark first, so a spreadsheet reads the file as UTF-8.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.StartsWith("uuid,time_utc", rows[0][1..], StringComparison.Ordinal);
        Assert.Equal(granted, rows.Length - 1);
        // The two exports made are audited; the one refused made nothing to audit.
        Assert.Equal(2, await AsSystemAsync(app, (db, _) => db.AuditEvents.CountAsync(e => e.Action == "calls.export", Ct)));
    }

    [Fact]
    public async Task The_parquet_export_holds_the_same_calls_with_typed_columns()
    {
        await using var app = await ImportedAsync();
        var (browser, did) = await ViewerAsync(app);
        using var _ = browser;

        var bytes = await browser.GetByteArrayAsync(new Uri("/calls/export.parquet?from=2025-12-01&to=2026-09-30", UriKind.Relative), Ct);
        var csv = await browser.GetStringAsync(new Uri("/calls/export.csv?from=2025-12-01&to=2026-09-30", UriKind.Relative), Ct);

        using var stream = new MemoryStream(bytes);
        var file = await Parquet.Serialization.ParquetSerializer.DeserializeAsync<ExportEndpoints.ParquetRow>(stream, cancellationToken: Ct);
        var fields = file.Schema.DataFields.ToDictionary(f => f.Name, f => f.ClrType);
        var rows = file.Data;
        var granted = await AsSystemAsync(app, (db, _) => db.Calls.Where(c => c.Lines.Any(l => l.Kind == LineKind.Did && l.Key == did))
            .Select(c => c.TalkUuid).OrderBy(u => u).ToListAsync(Ct));

        Assert.Equal(typeof(DateTime), fields["time_utc"]);
        Assert.Equal(typeof(int), fields["duration_seconds"]);
        Assert.Equal(typeof(bool), fields["has_recording"]);
        Assert.Equal(granted, rows.Select(r => r.Uuid).Order(StringComparer.Ordinal).ToList());
        Assert.Equal(csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length - 1, rows.Count);
        Assert.Equal(2, await AsSystemAsync(app, (db, _) => db.AuditEvents.CountAsync(e => e.Action == "calls.export", Ct)));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("+441144960042", "+441144960042")]
    [InlineData("Smith, J", "\"Smith, J\"")]
    [InlineData("plain", "plain")]
    public void Cells_a_spreadsheet_would_run_are_neutralised_and_numbers_are_not(string value, string cell) =>
        Assert.Equal(cell, ExportEndpoints.Cell(value));
}

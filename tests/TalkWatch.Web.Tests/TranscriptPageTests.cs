using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class TranscriptPageTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string ViewerPassword = "a long enough password";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    private static async Task<T> DbAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    private static async Task PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        using var content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value))));
        await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
    }

    /// <summary>The app with the captured calls stored and their transcripts copied, and one transcribed call to look at.</summary>
    private async Task<(WebApplicationFactory<Program> App, CallTranscript Transcript, string CallUuid, string Line)> CopiedAsync()
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        await app.Services.GetRequiredService<TranscriptSync>().SyncAsync(Ct);
        var (transcript, uuid, line) = await DbAsync(app, async db =>
        {
            var t = await db.CallTranscripts.OrderBy(t => t.TalkId).FirstAsync(Ct);
            var call = await db.Calls.Include(c => c.Lines).SingleAsync(c => c.Id == t.CallId, Ct);
            return (t, call.TalkUuid, call.Lines.First(l => l.Kind == Core.Calls.LineKind.Did));
        });
        return (app, transcript, uuid, $"{line.Kind}:{line.Key}");
    }

    private static string AWord(CallTranscript transcript) => transcript.Text.Split([' ', '\n', '.', ','], StringSplitOptions.RemoveEmptyEntries).First(w => w.Length > 4);

    [Fact]
    public async Task Transcripts_are_copied_once_for_the_calls_they_belong_to_and_read_on_the_call_page_with_an_audit_entry()
    {
        var (app, transcript, uuid, _) = await CopiedAsync();
        await using var _ = app;
        var copiedAt = await DbAsync(app, db => db.CallTranscripts.ToDictionaryAsync(t => t.Id, t => t.CopiedAt, Ct));

        // A second pass finds nothing new, and changes nothing.
        await app.Services.GetRequiredService<TranscriptSync>().SyncAsync(Ct);
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var page = WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri($"/calls/{uuid}", UriKind.Relative), Ct));
        var list = await admin.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal(13, copiedAt.Count);
        Assert.Equal(copiedAt, await DbAsync(app, db => db.CallTranscripts.ToDictionaryAsync(t => t.Id, t => t.CopiedAt, Ct)));
        Assert.Contains("data-transcript", page, StringComparison.Ordinal);
        Assert.Contains(TranscriptStore.LinesOf(transcript)[0].Text, page, StringComparison.Ordinal);
        Assert.Equal(1, await DbAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "transcript.read", Ct)));
        Assert.Contains("data-has-transcript", list, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_a_grant_that_allows_transcripts_shows_one_or_finds_it_in_a_search()
    {
        var (app, transcript, uuid, line) = await CopiedAsync();
        await using var _ = app;
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        await PostAsync(admin, "/admin/users", "/admin/users", ("Username", "reader"), ("Role", Roles.Viewer), ("Password", ViewerPassword));
        var reader = await DbAsync(app, db => db.Users.Where(u => u.UserName == "reader").Select(u => u.Id).SingleAsync(Ct));
        await PostAsync(admin, $"/admin/users/{reader}", $"/admin/users/{reader}/grants", ("Line", line));
        using var viewer = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(viewer, "reader", ViewerPassword);
        var search = $"/calls?q={Uri.EscapeDataString(AWord(transcript))}";

        var withoutGrant = await viewer.GetStringAsync(new Uri($"/calls/{uuid}", UriKind.Relative), Ct);
        var foundWithout = await viewer.GetStringAsync(new Uri(search, UriKind.Relative), Ct);
        await DbAsync(app, async db =>
        {
            await db.Grants.Where(g => g.UserId == reader).ExecuteUpdateAsync(u => u.SetProperty(g => g.AllowTranscripts, true), Ct);
            return 0;
        });
        var withGrant = await viewer.GetStringAsync(new Uri($"/calls/{uuid}", UriKind.Relative), Ct);
        var foundWith = await viewer.GetStringAsync(new Uri(search, UriKind.Relative), Ct);

        // The call itself they may see either way; what was said, only with the grant.
        Assert.Contains("data-outcome", withoutGrant, StringComparison.Ordinal);
        Assert.DoesNotContain("data-transcript", withoutGrant, StringComparison.Ordinal);
        Assert.DoesNotContain($"data-call=\"{uuid}\"", foundWithout, StringComparison.Ordinal);
        Assert.Contains("data-transcript", withGrant, StringComparison.Ordinal);
        Assert.Contains($"data-call=\"{uuid}\"", foundWith, StringComparison.Ordinal);
        Assert.Contains($"data-call=\"{uuid}\"", await admin.GetStringAsync(new Uri(search, UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_copying_turned_off_no_transcript_is_kept()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory),
            settings: new Dictionary<string, string> { ["Talk:CopyTranscripts"] = "false" });
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        await app.Services.GetRequiredService<TranscriptSync>().SyncAsync(Ct);

        Assert.Equal(0, await DbAsync(app, db => db.CallTranscripts.CountAsync(Ct)));
    }
}

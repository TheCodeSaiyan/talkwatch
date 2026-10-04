using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class RecordingTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task PollAsync(WebApplicationFactory<Program> app) =>
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    /// <summary>A viewer granted the DID a call came in on, with or without the recordings flag.</summary>
    private static async Task ViewerAsync(WebApplicationFactory<Program> app, string username, Guid callId, bool allowRecordings)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();

        var viewer = new AppUser { Id = Guid.NewGuid(), UserName = username, SiteId = site.Id };
        Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
        await users.AddToRoleAsync(viewer, Roles.Viewer);
        var line = await db.CallLines.Where(l => l.CallId == callId && l.Kind == LineKind.Did).FirstAsync(Ct);
        db.Grants.Add(new Grant
        {
            Id = Guid.NewGuid(), SiteId = site.Id, UserId = viewer.Id, Kind = line.Kind, Key = line.Key,
            AllowRecordings = allowRecordings, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    private static async Task<AudioFile> AnyCopiedWithDidAsync(WebApplicationFactory<Program> app) =>
        await AsSystemAsync(app, db => db.AudioFiles
            .Where(a => a.State == AudioState.Copied && db.CallLines.Any(l => l.CallId == a.CallId && l.Kind == LineKind.Did))
            .FirstAsync(Ct));

    [Fact]
    public async Task Recordings_are_copied_once_with_their_checksum_and_missing_ones_are_not_retried()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        await using var app = talkwatch.Create(console);

        await PollAsync(app);
        var rows = await AsSystemAsync(app, db => db.AudioFiles.ToListAsync(Ct));
        var store = app.Services.GetRequiredService<AudioStore>();

        var copied = rows.Where(r => r.State == AudioState.Copied).ToList();
        Assert.Equal(console.RecordingUuids.Count, copied.Count);
        Assert.All(copied, r =>
        {
            using var file = store.OpenRead(r.RelativePath!);
            Assert.Equal(r.Sha256, Convert.ToHexStringLower(SHA256.HashData(file)));
        });
        Assert.Contains(rows, r => r.State == AudioState.Unavailable);

        console.Requests.Clear();
        await PollAsync(app);
        Assert.DoesNotContain(console.Requests, r => r.Contains("/recording/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_recording_that_fails_is_retried_on_a_later_poll()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory) { RecordingStatus = HttpStatusCode.InternalServerError };
        await using var app = talkwatch.Create(console);

        await PollAsync(app);
        Assert.True(await AsSystemAsync(app, db => db.AudioFiles.AllAsync(a => a.State == AudioState.Failed && a.Attempts == 1, Ct)));

        console.RecordingStatus = null;
        await PollAsync(app);
        Assert.Equal(console.RecordingUuids.Count, await AsSystemAsync(app, db => db.AudioFiles.CountAsync(a => a.State == AudioState.Copied, Ct)));
    }

    [Fact]
    public async Task An_admin_can_play_and_seek_and_one_play_is_audited_once()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);
        var audio = await AnyCopiedWithDidAsync(app);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        using var first = new HttpRequestMessage(HttpMethod.Get, new Uri($"/audio/{audio.Id}", UriKind.Relative)) { Headers = { Range = new(0, 99) } };
        var start = await browser.SendAsync(first, Ct);
        using var later = new HttpRequestMessage(HttpMethod.Get, new Uri($"/audio/{audio.Id}", UriKind.Relative)) { Headers = { Range = new(100, 199) } };
        var seek = await browser.SendAsync(later, Ct);

        Assert.Equal(HttpStatusCode.PartialContent, start.StatusCode);
        Assert.Equal("audio/mpeg", start.Content.Headers.ContentType!.MediaType);
        Assert.Equal(100, (await start.Content.ReadAsByteArrayAsync(Ct)).Length);
        Assert.Equal(HttpStatusCode.PartialContent, seek.StatusCode);
        Assert.Equal(1, await AsSystemAsync(app, db => db.AuditEvents.CountAsync(e => e.Action == "recording.play" && e.TargetId == audio.Id.ToString(), Ct)));
    }

    private const string VoicemailOwner = "abe3a229-7539-48dd-a8e3-e23fd4d9f72f";

    /// <summary>
    /// The call behind the captured voicemail, as the console recorded it: through the switchboard menu to a user's
    /// voicemail, marked 'accepted'. Its numbers are the fictional ones the capture's pseudonymiser gave it.
    /// </summary>
    private static string VoicemailCall(string uuid) => $$$"""
        {"uuid": "{{{uuid}}}", "time": "2026-09-30T16:57:17.85Z", "direction": "in", "status": "accepted", "duration": 60,
         "from": "+447700900774", "to": "+441174960404", "to_smart_attendant_id": "15",
         "call_events": [
           {"time": "2026-09-30T16:57:17.898Z", "event": "call_started", "event_data": {"to_smart_attendant_id": 15}},
           {"time": "2026-09-30T16:57:57.895Z", "event": "call_sent_to_voicemail", "event_data": {"recipient_user_uuids": ["{{{VoicemailOwner}}}"]}},
           {"time": "2026-09-30T16:58:18.224Z", "event": "vm_msg_recorded", "event_data": {"recipient_user_uuids": ["{{{VoicemailOwner}}}"]}},
           {"time": "2026-09-30T16:58:18.313Z", "event": "call_hangup", "event_data": {"hangup_cause": "normal_end"}}
         ]}
        """;

    private static async Task GrantVoicemailOwnerAsync(WebApplicationFactory<Program> app, string username, bool allowVoicemail)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var viewer = new AppUser { Id = Guid.NewGuid(), UserName = username, SiteId = site.Id };
        Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
        await users.AddToRoleAsync(viewer, Roles.Viewer);
        db.Grants.Add(new Grant
        {
            Id = Guid.NewGuid(), SiteId = site.Id, UserId = viewer.Id, Kind = LineKind.User, Key = VoicemailOwner,
            AllowVoicemail = allowVoicemail, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task A_voicemail_is_copied_and_heard_only_by_whoever_may_hear_that_persons_voicemail()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.AddCall(VoicemailCall("ce248ac4-a417-4b2b-8d8f-4013b1518a61"));
        await using var app = talkwatch.Create(console);
        await PollAsync(app);

        var voicemail = await AsSystemAsync(app, db => db.AudioFiles.SingleAsync(a => a.Kind == AudioKind.Voicemail, Ct));
        var captured = Path.Combine(FixtureConsole.DefaultDirectory + "-voicemail", "audio", "0003.mp3");
        Assert.Equal(AudioState.Copied, voicemail.State);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(captured, Ct))), voicemail.Sha256);

        await GrantVoicemailOwnerAsync(app, "owner", allowVoicemail: true);
        await GrantVoicemailOwnerAsync(app, "colleague", allowVoicemail: false);
        using var owner = TalkWatchApp.Browser(app);
        using var colleague = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(owner, "owner", "a long enough password");
        await TalkWatchApp.SignInAsync(colleague, "colleague", "a long enough password");

        // Both see the call, through the voicemail owner's line; only the one allowed voicemail can hear it.
        var ownerPage = await owner.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        var colleaguePage = await colleague.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        Assert.Contains($"data-audio=\"{voicemail.Id}\" data-kind=\"Voicemail\"", ownerPage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(new Uri($"/audio/{voicemail.Id}", UriKind.Relative), Ct)).StatusCode);
        Assert.Contains("data-call=\"ce248ac4-a417-4b2b-8d8f-4013b1518a61\"", colleaguePage, StringComparison.Ordinal);
        Assert.DoesNotContain("data-audio=", colleaguePage, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await colleague.GetAsync(new Uri($"/audio/{voicemail.Id}", UriKind.Relative), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_voicemail_the_console_no_longer_has_is_marked_and_not_fetched_again()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        console.AddCall(VoicemailCall("00000000-0000-4000-8000-00000000cafe"));
        await using var app = talkwatch.Create(console);

        await PollAsync(app);
        await PollAsync(app);

        var voicemail = await AsSystemAsync(app, db => db.AudioFiles.SingleAsync(a => a.Kind == AudioKind.Voicemail, Ct));
        Assert.Equal((AudioState.Unavailable, 1), (voicemail.State, voicemail.Attempts));
        Assert.Equal(1, console.Requests.Count(r => r == "/proxy/talk/api/voicemail/data/00000000-0000-4000-8000-00000000cafe"));
    }

    [Fact]
    public async Task Without_the_recordings_flag_a_viewer_sees_the_call_but_cannot_hear_it()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);
        var audio = await AnyCopiedWithDidAsync(app);
        await ViewerAsync(app, "listener", audio.CallId, allowRecordings: false);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "listener", "a long enough password");

        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        var play = await browser.GetAsync(new Uri($"/audio/{audio.Id}", UriKind.Relative), Ct);

        Assert.True(TalkWatchApp.CallRows(page) > 0);
        Assert.DoesNotContain("data-audio=", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, play.StatusCode);
    }

    [Fact]
    public async Task With_the_recordings_flag_a_viewer_can_play_their_calls_recordings()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);
        var audio = await AnyCopiedWithDidAsync(app);
        await ViewerAsync(app, "listener", audio.CallId, allowRecordings: true);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "listener", "a long enough password");

        var page = await browser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);
        var play = await browser.GetAsync(new Uri($"/audio/{audio.Id}", UriKind.Relative), Ct);

        Assert.Contains($"data-audio=\"{audio.Id}\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, play.StatusCode);
    }

    [Fact]
    public async Task Signed_out_the_audio_sends_you_to_sign_in()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);
        var audio = await AnyCopiedWithDidAsync(app);
        using var browser = TalkWatchApp.Browser(app);

        var play = await browser.GetAsync(new Uri($"/audio/{audio.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.Redirect, play.StatusCode);
    }
}

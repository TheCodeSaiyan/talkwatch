using System.Net;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Replay;

namespace TalkWatch.Web.Tests;

public sealed partial class RecordingTests
{
    // The type a recording is served as is the console's word, kept from when it was copied. Served as a page, a
    // "recording" would be one on TalkWatch's own address; so anything but audio is served as a plain download.
    [Fact]
    public async Task A_recording_is_served_as_audio_or_as_a_download_never_as_a_page()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PollAsync(app);
        var audio = await AnyCopiedWithDidAsync(app);
        Assert.Equal(1, await AsSystemAsync(app, db => db.AudioFiles.Where(a => a.Id == audio.Id).ExecuteUpdateAsync(a => a.SetProperty(x => x.ContentType, "text/html"), Ct)));
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var served = await browser.GetAsync(new Uri($"/audio/{audio.Id}", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal("application/octet-stream", served.Content.Headers.ContentType?.MediaType);
    }
}

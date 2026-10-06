using System.Net;
using System.Text.RegularExpressions;
using TalkWatch.Replay;

namespace TalkWatch.Web.Tests;

/// <summary>The note a page shows after a form is one TalkWatch wrote, never words a link put in the address.</summary>
public sealed partial class PageMessagesTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    [Fact]
    public async Task A_note_from_a_link_is_not_shown_and_one_from_a_form_is()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var forged = await admin.GetStringAsync(new Uri("/admin/users?msg=Your%20session%20expired%2C%20sign%20in%20at%20evil.example", UriKind.Relative), Ct);
        Assert.DoesNotContain("Your session expired", forged, StringComparison.Ordinal);

        // A person sent without a password: the page says what's missing, as it always did.
        using var form = new FormUrlEncodedContent([new("Username", "someone"), new("Role", "Viewer"),
            new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(forged).Groups[1].Value))]);
        var sent = await admin.PostAsync(new Uri("/admin/users", UriKind.Relative), form, Ct);
        var back = sent.Headers.Location!.OriginalString;
        Assert.Contains("sig=", back, StringComparison.Ordinal);
        Assert.Contains("Give a username, a role and a first password.", WebUtility.HtmlDecode(await admin.GetStringAsync(new Uri(back, UriKind.Relative), Ct)), StringComparison.Ordinal);
    }
}

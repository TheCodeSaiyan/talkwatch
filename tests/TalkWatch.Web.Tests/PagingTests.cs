using System.Net;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using TalkWatch.Replay;

namespace TalkWatch.Web.Tests;

/// <summary>A page number past any list is an empty page, not a server error.</summary>
public sealed partial class PagingTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"<code class=""secret-box"" data-new-token>([^<]+)</code>")]
    private static partial Regex NewToken();

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex FormToken();

    [Fact]
    public async Task A_page_number_too_big_to_multiply_is_an_empty_page()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var account = await browser.GetStringAsync(new Uri("/account", UriKind.Relative), Ct);
        using var form = new FormUrlEncodedContent([new("_handler", "new-token"), new("Token.Name", "script"),
            new("__RequestVerificationToken", WebUtility.HtmlDecode(FormToken().Match(account).Groups[1].Value))]);
        var made = await (await browser.PostAsync(new Uri("/account", UriKind.Relative), form, Ct)).Content.ReadAsStringAsync(Ct);
        using var api = app.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WebUtility.HtmlDecode(NewToken().Match(made).Groups[1].Value));

        var fromApi = await api.GetAsync(new Uri("/api/v1/calls?page=2147483647&pageSize=500", UriKind.Relative), Ct);
        var fromPage = await browser.GetAsync(new Uri("/calls?page=2147483647", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, fromApi.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fromPage.StatusCode);
    }
}

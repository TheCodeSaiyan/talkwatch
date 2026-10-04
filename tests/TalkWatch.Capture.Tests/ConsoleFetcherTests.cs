using System.Net;
using System.Text;
using TalkWatch.Capture;

namespace TalkWatch.Capture.Tests;

public class ConsoleFetcherTests
{
    private sealed class FakeConsole(HttpStatusCode signInStatus) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Csrf, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery,
                request.Headers.TryGetValues("X-Csrf-Token", out var v) ? v.Single() : null, body));

            if (request.RequestUri.AbsolutePath == "/api/auth/login")
            {
                var login = new HttpResponseMessage(signInStatus);
                login.Headers.Add("X-Csrf-Token", "csrf-1");
                return login;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"calls":[]}""", Encoding.UTF8, "application/json") };
            response.Headers.Add("X-Updated-Csrf-Token", "csrf-2");
            return response;
        }
    }

    [Fact]
    public async Task Signs_in_then_sends_the_latest_csrf_token_with_each_request()
    {
        var console = new FakeConsole(HttpStatusCode.OK);
        using var fetcher = new ConsoleFetcher(new Uri("https://udm.test"), console);
        var ct = TestContext.Current.CancellationToken;

        await fetcher.SignInAsync("talkwatch", "s3cret-pw", ct);
        var first = await fetcher.GetAsync("/proxy/talk/api/calls?page=1", ct);
        await fetcher.GetAsync("/proxy/talk/api/calls?page=2", ct);

        Assert.Equal(("/api/auth/login", HttpMethod.Post), (console.Requests[0].Path, console.Requests[0].Method));
        Assert.Contains("\"username\":\"talkwatch\"", console.Requests[0].Body, StringComparison.Ordinal);
        Assert.Equal("csrf-1", console.Requests[1].Csrf);
        Assert.Equal("csrf-2", console.Requests[2].Csrf);
        Assert.Equal((200, "application/json", """{"calls":[]}"""), (first.Status, first.ContentType, Encoding.UTF8.GetString(first.Body)));
    }

    [Fact]
    public async Task A_refused_sign_in_stops_with_the_status_and_not_the_password()
    {
        using var fetcher = new ConsoleFetcher(new Uri("https://udm.test"), new FakeConsole(HttpStatusCode.Unauthorized));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fetcher.SignInAsync("talkwatch", "s3cret-pw", TestContext.Current.CancellationToken));

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret-pw", error.Message, StringComparison.Ordinal);
    }
}

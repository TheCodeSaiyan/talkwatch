using System.Text;
using System.Text.Json;
using TalkWatch.Capture;

namespace TalkWatch.Capture.Tests;

public class WebhookRecorderTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 12, 9, 0, 0, TimeSpan.Zero);

    private static Task<IReadOnlyList<CaptureEntry>?> Read(string request) =>
        WebhookRecorder.ReadAsync(new MemoryStream(Encoding.Latin1.GetBytes(request)), At, CancellationToken.None);

    [Fact]
    public async Task A_post_keeps_its_body_and_headers()
    {
        var entries = await Read(
            "POST /hook HTTP/1.1\r\nHost: pc:8787\r\nContent-Type: application/json\r\nX-Signature: abc\r\nContent-Length: 17\r\n\r\n{\"event\":\"call\"}\n");

        Assert.NotNull(entries);
        Assert.Collection(entries,
            hook =>
            {
                Assert.Equal(("hook", "POST", "/hook", "application/json"), (hook.Kind, hook.Method, hook.Path, hook.ContentType));
                Assert.Equal("{\"event\":\"call\"}\n", Encoding.UTF8.GetString(hook.Body));
            },
            headers =>
            {
                Assert.Equal("hook-headers", headers.Kind);
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(headers.Body)!;
                Assert.Equal("abc", map["x-signature"]);
                Assert.Equal("17", map["content-length"]);
            });
    }

    [Fact]
    public async Task A_get_keeps_its_query_and_has_no_body()
    {
        var entries = await Read("GET /hook?event=call_started&from=%2B447700900123 HTTP/1.1\r\nHost: pc\r\n\r\n");

        var hook = Assert.Single(entries!, e => e.Kind == "hook");
        Assert.Equal(("GET", "/hook?event=call_started&from=%2B447700900123"), (hook.Method, hook.Path));
        Assert.Empty(hook.Body);
    }

    [Fact]
    public async Task A_chunked_body_is_put_back_together()
    {
        var entries = await Read("POST /hook HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5\r\n{\"a\":\r\n3;x=y\r\n1}\n\r\n0\r\n\r\n");

        Assert.Equal("{\"a\":1}\n", Encoding.UTF8.GetString(entries![0].Body));
    }

    [Fact]
    public async Task A_connection_that_closes_before_a_request_records_nothing()
    {
        Assert.Null(await Read(""));
        Assert.Null(await Read("POST /hook HTTP/1.1\r\nHost:"));
    }

    [Fact]
    public async Task A_body_cut_short_is_refused()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read("POST /hook HTTP/1.1\r\nContent-Length: 50\r\n\r\n{}"));
    }
}

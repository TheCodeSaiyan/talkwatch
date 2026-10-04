using System.Text;
using TalkWatch.Capture;

namespace TalkWatch.Capture.Tests;

public class HarImporterTests
{
    private const string Password = "hunter2-secret-password";
    private const string Cookie = "TOKEN=eyJ-session-cookie";

    private static readonly string Har = """
        {"log":{"entries":[
          {"startedDateTime":"2026-09-29T12:00:00.000Z",
           "request":{"method":"POST","url":"https://udm.local/api/auth/login","headers":[],"postData":{"text":"{\"password\":\"@PASSWORD@\"}"}},
           "response":{"status":200,"headers":[{"name":"Set-Cookie","value":"@COOKIE@"}],"content":{"mimeType":"application/json","text":"{\"token\":\"x\"}"}}},
          {"startedDateTime":"2026-09-29T12:00:01.000Z",
           "request":{"method":"GET","url":"https://udm.local/proxy/talk/api/calls?page=1","headers":[{"name":"Cookie","value":"@COOKIE@"}]},
           "response":{"status":200,"headers":[],"content":{"mimeType":"application/json","text":"{\"calls\":[]}"}}},
          {"startedDateTime":"2026-09-29T12:00:02.000Z",
           "request":{"method":"GET","url":"https://udm.local/talk/assets/app.js","headers":[]},
           "response":{"status":200,"headers":[],"content":{"mimeType":"application/javascript","text":"var x=1"}}},
          {"startedDateTime":"2026-09-29T12:00:03.000Z",
           "request":{"method":"GET","url":"https://udm.local/proxy/talk/api/voicemail/1/audio","headers":[]},
           "response":{"status":200,"headers":[],"content":{"mimeType":"audio/wav","text":"UklGRg==","encoding":"base64"}}},
          {"startedDateTime":"2026-09-29T12:00:04.000Z",
           "request":{"method":"GET","url":"wss://udm.local/proxy/talk/ws","headers":[{"name":"Cookie","value":"@COOKIE@"}]},
           "response":{"status":101,"headers":[],"content":{"mimeType":"x-unknown"}},
           "_webSocketMessages":[{"type":"receive","time":1790000000.5,"opcode":1,"data":"{\"event\":\"ringing\"}"}]}
        ]}}
        """.Replace("@PASSWORD@", Password, StringComparison.Ordinal).Replace("@COOKIE@", Cookie, StringComparison.Ordinal);

    private static IReadOnlyList<CaptureEntry> Import() => HarImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(Har)));

    [Fact]
    public void Api_calls_and_websocket_messages_are_kept_and_assets_are_not()
    {
        var entries = Import();

        Assert.Collection(entries,
            e => Assert.Equal(("http", "GET", "/proxy/talk/api/calls?page=1"), (e.Kind, e.Method, e.Path)),
            e => Assert.Equal(("http", "/proxy/talk/api/voicemail/1/audio", "RIFF"), (e.Kind, e.Path, Encoding.ASCII.GetString(e.Body))),
            e => Assert.Equal(("ws", "receive", """{"event":"ringing"}"""), (e.Kind, e.Method, Encoding.UTF8.GetString(e.Body))));
    }

    [Fact]
    public void A_post_body_is_kept_as_its_own_entry_except_for_sign_in()
    {
        const string har = """
            {"log":{"entries":[
              {"startedDateTime":"2026-09-30T00:10:00.000Z",
               "request":{"method":"POST","url":"https://udm.local/proxy/talk/api/voicemail/recording","headers":[],"postData":{"mimeType":"application/json","text":"{\"uuid\":\"69bb7b56\"}"}},
               "response":{"status":200,"headers":[],"content":{"mimeType":"audio/mpeg","text":"SUQz","encoding":"base64"}}}
            ]}}
            """;

        var entries = HarImporter.Import(new MemoryStream(Encoding.UTF8.GetBytes(har)));

        Assert.Collection(entries,
            e => Assert.Equal(("req", "POST", "application/json", """{"uuid":"69bb7b56"}"""), (e.Kind, e.Method, e.ContentType, Encoding.UTF8.GetString(e.Body))),
            e => Assert.Equal(("http", "POST", "audio/mpeg"), (e.Kind, e.Method, e.ContentType)));
        Assert.DoesNotContain(Import(), e => e.Kind == "req");
    }

    [Fact]
    public void Sign_in_traffic_and_headers_never_reach_the_entries()
    {
        var everything = string.Join("\n", Import().Select(e => $"{e.Path} {e.ContentType} {Encoding.UTF8.GetString(e.Body)}"));

        Assert.DoesNotContain(Password, everything, StringComparison.Ordinal);
        Assert.DoesNotContain("session-cookie", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("/auth/", everything, StringComparison.Ordinal);
    }
}

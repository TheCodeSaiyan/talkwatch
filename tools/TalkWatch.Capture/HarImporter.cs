using System.Text;
using System.Text.Json;

namespace TalkWatch.Capture;

/// <summary>
/// Turns a browser HAR of the Talk UI into capture entries. A HAR records exactly the endpoints the UI calls,
/// including WebSocket traffic, which is more trustworthy than guessing undocumented paths.
/// </summary>
/// <remarks>
/// Only response bodies are kept. Request bodies and all headers are dropped, and anything under an auth path
/// is skipped, so the sign-in password, cookies and CSRF tokens never reach the archive.
/// </remarks>
public static class HarImporter
{
    private static readonly string[] SkippedExtensions =
        [".js", ".mjs", ".css", ".map", ".woff", ".woff2", ".ttf", ".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".html"];

    public static IReadOnlyList<CaptureEntry> Import(Stream har)
    {
        using var document = JsonDocument.Parse(har);
        var entries = new List<CaptureEntry>();

        foreach (var item in document.RootElement.GetProperty("log").GetProperty("entries").EnumerateArray())
        {
            var request = item.GetProperty("request");
            if (!Uri.TryCreate(request.GetProperty("url").GetString(), UriKind.Absolute, out var url) || !IsApi(url))
            {
                continue;
            }

            var at = item.TryGetProperty("startedDateTime", out var started) && started.TryGetDateTimeOffset(out var when) ? when : DateTimeOffset.MinValue;
            var path = url.PathAndQuery;

            if (item.TryGetProperty("_webSocketMessages", out var messages))
            {
                foreach (var message in messages.EnumerateArray())
                {
                    var data = message.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
                    var direction = message.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var sent = message.TryGetProperty("time", out var time) && time.TryGetDouble(out var seconds)
                        ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
                        : at;
                    entries.Add(new CaptureEntry("ws", direction, path, 101, null, sent, Encoding.UTF8.GetBytes(data)));
                }

                continue;
            }

            var response = item.GetProperty("response");
            var content = response.GetProperty("content");
            var mimeType = content.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null;
            var body = content.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "";
            var bytes = content.TryGetProperty("encoding", out var encoding) && encoding.GetString() == "base64"
                ? Convert.FromBase64String(body)
                : Encoding.UTF8.GetBytes(body);

            var method = request.GetProperty("method").GetString() ?? "GET";

            // What a POST sends can matter as much as what it returns: voicemail audio is fetched by a POST whose body
            // names the message. Kept as its own "req" entry. Auth paths never get this far.
            if (method != "GET" && request.TryGetProperty("postData", out var post) && post.TryGetProperty("text", out var postText)
                && postText.GetString() is { Length: > 0 } requestBody)
            {
                var postType = post.TryGetProperty("mimeType", out var pm) ? pm.GetString() : null;
                entries.Add(new CaptureEntry("req", method, path, 0, postType, at, Encoding.UTF8.GetBytes(requestBody)));
            }

            entries.Add(new CaptureEntry("http", method, path, response.GetProperty("status").GetInt32(), mimeType, at, bytes));
        }

        return entries;
    }

    private static bool IsApi(Uri url)
    {
        var path = url.AbsolutePath;
        if (path.Contains("/auth/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/auth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !SkippedExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase))
            && (path.Contains("/api/", StringComparison.OrdinalIgnoreCase) || path.Contains("/proxy/", StringComparison.OrdinalIgnoreCase)
                || url.Scheme is "ws" or "wss");
    }
}

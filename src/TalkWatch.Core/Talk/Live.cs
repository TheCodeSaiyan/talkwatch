using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkWatch.Core.Talk;

/// <summary>A Talk user from <c>GET /proxy/talk/api/users</c>: the directory that turns user ids into names and extensions.</summary>
public sealed record TalkUser
{
    /// <summary>The user's uuid. The same id call routing records as a User line, and a device's <c>user_id</c>.</summary>
    [JsonPropertyName("unique_id")]
    public required string Uuid { get; init; }

    /// <summary>Talk's numeric id for the user, which some events use instead of the uuid.</summary>
    public int? Id { get; init; }

    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? FullName { get; init; }
    public string? Ext { get; init; }

    /// <summary>The user's email, as Talk holds it: how a person signing in through the identity provider is matched to them.</summary>
    public string? Email { get; init; }

    public bool HasActiveCalls { get; init; }
    public bool HideFromUserList { get; init; }

    /// <summary>The number assigned to the user, if any.</summary>
    public string? Did { get; init; }

    /// <summary>Further numbers assigned to the user, as Talk sends them: numbers, or objects with a <c>did</c>.</summary>
    public JsonElement? DidList { get; init; }

    /// <summary>Whether any number is the user's own.</summary>
    public bool HasOwnNumber => !string.IsNullOrWhiteSpace(Did) || (DidList is { ValueKind: JsonValueKind.Array } list && list.EnumerateArray().Any(e =>
        e.ValueKind == JsonValueKind.String ? !string.IsNullOrWhiteSpace(e.GetString())
        : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("did", out var did) && did.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(did.GetString())));

    public string DisplayName => FullName is { Length: > 0 } full ? full : $"{FirstName} {LastName}".Trim() is { Length: > 0 } name ? name : Uuid;
}

/// <summary>A handset or softphone, as <c>DEVICES_UPDATED</c> reports it.</summary>
public sealed record TalkDevice
{
    public required string Mac { get; init; }
    public string? Model { get; init; }
    public string? DisplayName { get; init; }

    /// <summary>The Talk user the device is assigned to (uuid), or null.</summary>
    public string? UserId { get; init; }

    public string? Ext { get; init; }

    /// <summary>'online' or 'offline' as Talk reports it.</summary>
    public string? Status { get; init; }

    public bool? SipReg { get; init; }
    public DateTimeOffset? LastSeen { get; init; }
    public string? Version { get; init; }

    /// <summary>Whether Talk has a firmware update waiting for the device.</summary>
    public bool? UpdateAvailable { get; init; }
}

/// <summary>One message from Talk's live WebSocket: <c>{"event": "...", "data": ...}</c>.</summary>
public sealed record LiveMessage(string Event, JsonElement Data)
{
    public const string CallLogUpdated = "CALL_LOG_UPDATED";
    public const string CallEventsUpdated = "CALL_EVENTS_UPDATED";
    public const string DevicesUpdated = "DEVICES_UPDATED";
    public const string UserStoreUpdated = "USER_STORE_UPDATED";
    public const string UsersOnActiveCalls = "USERS_ON_ACTIVE_CALLS";

    /// <summary>Talk has finished (or failed) transcribing a call; the data is the transcript itself.</summary>
    public const string TranscriptUpdated = "AI_TRANSCRIBE_TASK_UPDATE";

    /// <summary>Parses a message; null for anything that is not an event TalkWatch can read.</summary>
    public static LiveMessage? Parse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("event", out var e) && e.GetString() is { } name
                ? new LiveMessage(name, root.TryGetProperty("data", out var data) ? data.Clone() : default)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The call records carried by <see cref="CallLogUpdated"/>, in the same shape as the call log.</summary>
    /// <summary>The transcript an <see cref="TranscriptUpdated"/> message carries, or null for any other message.</summary>
    public TalkTranscript? Transcript() => Event == TranscriptUpdated ? TalkTranscript.Parse(Data) : null;

    /// <summary>
    /// For <see cref="CallEventsUpdated"/>: the call whose events changed, as its uuid. Talk sends nothing else, so the
    /// events themselves are read with <see cref="TalkClient.GetCallEventsAsync"/>.
    /// </summary>
    public string? CallId() =>
        Event == CallEventsUpdated && Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("call_id", out var id) && id.ValueKind == JsonValueKind.String
            ? id.GetString()
            : null;

    public IReadOnlyList<CallLogRecord> CallRecords() =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array
            ? records.Deserialize<List<CallLogRecord>>(TalkJson.Options) ?? []
            : [];

    public IReadOnlyList<TalkDevice> Devices() =>
        Data.ValueKind == JsonValueKind.Array ? Data.Deserialize<List<TalkDevice>>(TalkJson.Options) ?? [] : [];

    /// <summary>For <see cref="UserStoreUpdated"/>: which user, their presence status, and whether they are on a call.</summary>
    public (string UserUuid, string? Status, bool? OnCall)? UserPresence()
    {
        if (Data.ValueKind != JsonValueKind.Object || !Data.TryGetProperty("user_uuid", out var uuid) || uuid.GetString() is not { } id)
        {
            return null;
        }

        var item = Data.TryGetProperty("item", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        string? status = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        bool? onCall = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("has_active_calls", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False ? a.GetBoolean() : null;
        return (id, status, onCall);
    }

    /// <summary>For <see cref="UsersOnActiveCalls"/>: every user id on a call right now, as Talk sends them.</summary>
    public IReadOnlyList<string> UsersOnCalls() =>
        Data.ValueKind == JsonValueKind.Object && Data.TryGetProperty("user_ids", out var ids) && ids.ValueKind == JsonValueKind.Array
            ? [.. ids.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText())]
            : [];
}

/// <summary>
/// Talk's live WebSocket (<c>/proxy/talk/</c>). Connects through the same HTTP handler as <see cref="TalkClient"/>, so
/// it carries the same session cookie and trusts the console's certificate the same way.
/// </summary>
public static class TalkLive
{
    public static async IAsyncEnumerable<LiveMessage> ReadAsync(Uri console, HttpMessageInvoker invoker, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var uri = new UriBuilder(console) { Scheme = console.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", Path = "/proxy/talk/" }.Uri;
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        await socket.ConnectAsync(uri, invoker, cancellationToken);

        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                yield break;
            }

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
            {
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            if (result.MessageType == WebSocketMessageType.Text && LiveMessage.Parse(text) is { } parsed)
            {
                yield return parsed;
            }
        }
    }
}

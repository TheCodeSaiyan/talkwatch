using System.Text.Json;
using System.Text.Json.Serialization;

namespace TalkWatch.Core.Talk;

/// <summary>One page of <c>GET /proxy/talk/api/call_log</c>, newest first.</summary>
public sealed record CallLogPage(IReadOnlyList<CallLogRecord> Records, int TotalCount)
{
    /// <summary>The page exactly as Talk sent it, kept so the data can be reprocessed after a fix.</summary>
    [JsonIgnore]
    public string RawJson { get; init; } = "";
}

/// <summary>
/// A call as Talk records it. Every call carries its routing events inline, which is what line grants are derived from.
/// </summary>
/// <remarks>
/// Shapes are from UniFi OS 5.1.33 and Talk 5.3.2. Id fields accept a JSON number or a string: some were only ever
/// null in the captures, and an id changing type is exactly the kind of drift a firmware update brings.
/// </remarks>
public sealed record CallLogRecord
{
    public required string Uuid { get; init; }
    public required DateTimeOffset Time { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }
    public string? AnsweredBy { get; init; }
    // Null for some calls on a real console (seen on Talk 5.3.2); stored as 'unknown' rather than refused.
    public string? Status { get; init; }
    public string? Direction { get; init; }
    // Null while a call is still in progress (seen in live updates); later updates fill it in.
    public int? Duration { get; init; }
    public bool? Recording { get; init; }
    public string? RecordingFilename { get; init; }
    public string? Country { get; init; }
    public int? QualityScore { get; init; }
    public bool IsVideoCall { get; init; }
    public bool IsIntercomCall { get; init; }
    public bool IsGroupIntercomCall { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))] public string? FromId { get; init; }
    public string? FromMac { get; init; }
    public string? FromDid { get; init; }
    public string? FromCallerName { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? FromContactId { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))] public string? ToId { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? ToGroupId { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? ToContactId { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? ToQueueId { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? ToSmartAttendantId { get; init; }
    public string? ToSmartAttendantTitle { get; init; }

    [JsonConverter(typeof(FlexibleIdConverter))] public string? AnsweredByUserUuid { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? AnsweredByContactId { get; init; }
    [JsonConverter(typeof(FlexibleIdConverter))] public string? AnsweredByGroupId { get; init; }
    public string? AnsweredByMac { get; init; }

    public IReadOnlyList<CallEvent> CallEvents { get; init; } = [];
}

/// <summary>A routing event within a call: call_started, seq_call_trying_endpoints, call_accepted, skipped_endpoints, call_hangup.</summary>
public sealed record CallEvent
{
    public required DateTimeOffset Time { get; init; }
    public required string Event { get; init; }
    public JsonElement? EventData { get; init; }
    public string? EventUuid { get; init; }

    public string? Text(string field) =>
        EventData is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty(field, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    public IEnumerable<string> List(string field) =>
        EventData is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)
            : [];
}

/// <summary>Reads an id that may arrive as a JSON number or a string, and holds it as a string.</summary>
public sealed class FlexibleIdConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
        JsonTokenType.Null => null,
        _ => throw new JsonException($"Expected an id as a number or string, not {reader.TokenType}."),
    };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}

public static class TalkJson
{
    /// <summary>Talk's JSON is snake_case throughout.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}

/// <summary>
/// A voicemail message's details (GET /proxy/talk/api/voicemail/data/{call uuid}), as Talk 5.3.2 sends them: numbers
/// arrive as strings, and received_at is Unix seconds.
/// </summary>
public sealed record VoicemailInfo
{
    public string? Uuid { get; init; }

    /// <summary>Where the message is kept on the console; POSTed back to fetch the audio.</summary>
    public string? FilePath { get; init; }

    public string? Duration { get; init; }
    public string? ReceivedAt { get; init; }
    public string? ReadAt { get; init; }
    public string? VmLeftForExt { get; init; }
    public string? VmReceiverUuid { get; init; }
}

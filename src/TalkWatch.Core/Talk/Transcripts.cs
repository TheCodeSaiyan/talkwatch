using System.Globalization;
using System.Text.Json;

namespace TalkWatch.Core.Talk;

/// <summary>One line of a transcript: who spoke, what they said, and when in the call, in seconds.</summary>
public sealed record TranscriptLine(string? Speaker, string Text, double? Start, double? End);

/// <summary>
/// A transcript Talk made of a call (GET /proxy/talk/api/transcript, and the AI_TRANSCRIBE_TASK_UPDATE live message),
/// as Talk 5.3.2 sends it: the lines, Talk's own summary, and a sentiment score with its class (positive, neutral or
/// negative). Only a completed transcript has any of these.
/// </summary>
public sealed record TalkTranscript(
    string Id, string? Status, string CallUuid, string? Summary, double? Sentiment, string? SentimentClass, IReadOnlyList<TranscriptLine> Lines)
{
    public const string Completed = "COMPLETED";

    public bool IsComplete => Status == Completed && Lines.Count > 0;

    /// <summary>One transcript object; null when it names no call. Throws <see cref="JsonException"/> on another shape.</summary>
    public static TalkTranscript? Parse(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Text(item, "id") is not { } id
            || !item.TryGetProperty("context", out var context) || Text(context, "call_uuid") is not { } call)
        {
            return null;
        }

        var lines = new List<TranscriptLine>();
        string? summary = null, sentimentClass = null;
        double? sentiment = null;
        if (item.TryGetProperty("analytics", out var analytics) && analytics.ValueKind == JsonValueKind.Object)
        {
            summary = Text(analytics, "summary");
            sentiment = Number(analytics, "sentiment");
            sentimentClass = Text(analytics, "sentimentClassification");
            if (analytics.TryGetProperty("lines", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var line in list.EnumerateArray())
                {
                    if (Text(line, "line") is { Length: > 0 } text)
                    {
                        lines.Add(new TranscriptLine(Text(line, "speaker"), text, Number(line, "line_start_time"), Number(line, "line_end_time")));
                    }
                }
            }
        }

        return new TalkTranscript(id, Text(item, "status"), call, summary, sentiment, sentimentClass?.ToLowerInvariant(), lines);
    }

    /// <summary>A page of the transcript list: its transcripts, and how many there are in all.</summary>
    public static (IReadOnlyList<TalkTranscript> Transcripts, int Total) ParsePage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var transcripts = root.GetProperty("transcripts").EnumerateArray().Select(Parse).OfType<TalkTranscript>().ToList();
        return (transcripts, root.TryGetProperty("total_count", out var total) && total.TryGetInt32(out var t) ? t : transcripts.Count);
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Times arrive as numbers in some responses and as strings ("0.59") in others.
    private static double? Number(JsonElement node, string name) =>
        !node.TryGetProperty(name, out var value) ? null
        : value.ValueKind == JsonValueKind.Number ? value.GetDouble()
        : value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d
        : null;
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Talk;

namespace TalkWatch.Data;

/// <summary>
/// Talk's transcript of a call, copied: what was said, Talk's summary and its sentiment. Readable only through a grant
/// on one of the call's lines that allows transcripts, the most sensitive thing TalkWatch holds.
/// </summary>
public sealed class CallTranscript
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid CallId { get; set; }

    /// <summary>Talk's id for the transcript.</summary>
    public required string TalkId { get; set; }

    public string? Summary { get; set; }
    public double? Sentiment { get; set; }

    /// <summary>positive, neutral or negative, as Talk classed it.</summary>
    public string? SentimentClass { get; set; }

    /// <summary>The lines, as <see cref="TranscriptLine"/> JSON.</summary>
    public required string Lines { get; set; }

    /// <summary>Every line's words in one string, for searching.</summary>
    public required string Text { get; set; }

    public DateTimeOffset CopiedAt { get; set; }
}

public static class TranscriptStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<TranscriptLine> LinesOf(CallTranscript transcript) =>
        JsonSerializer.Deserialize<List<TranscriptLine>>(transcript.Lines, Json) ?? [];

    /// <summary>
    /// Stores completed transcripts, new or changed, for calls TalkWatch already has; one for a call not yet stored is
    /// left for a later pass. Returns the calls that gained a transcript, for alerting on, and how many changed in all.
    /// </summary>
    public static async Task<(IReadOnlyList<(Guid CallId, CallTranscript Transcript)> Added, int Changed)> UpsertAsync(
        TalkWatchDbContext db, Guid siteId, IEnumerable<TalkTranscript> transcripts, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var complete = transcripts.Where(t => t.IsComplete).GroupBy(t => t.CallUuid).Select(g => g.First()).ToList();
        if (complete.Count == 0)
        {
            return ([], 0);
        }

        var uuids = complete.Select(t => t.CallUuid).ToList();
        var calls = await db.Calls.Where(c => c.SiteId == siteId && uuids.Contains(c.TalkUuid)).Select(c => new { c.Id, c.TalkUuid }).ToDictionaryAsync(c => c.TalkUuid, c => c.Id, cancellationToken);
        var callIds = calls.Values.ToList();
        var existing = await db.CallTranscripts.Where(t => callIds.Contains(t.CallId)).ToDictionaryAsync(t => t.CallId, cancellationToken);

        var added = new List<(Guid, CallTranscript)>();
        var changed = 0;
        foreach (var transcript in complete)
        {
            if (!calls.TryGetValue(transcript.CallUuid, out var callId))
            {
                continue;
            }

            var lines = JsonSerializer.Serialize(transcript.Lines, Json);
            var text = string.Join('\n', transcript.Lines.Select(l => l.Text));
            if (existing.TryGetValue(callId, out var row))
            {
                // Compared by its words, not its JSON: jsonb gives the lines back with their keys in its own order.
                if (row.TalkId == transcript.Id && row.Text == text && row.Summary == transcript.Summary && row.SentimentClass == transcript.SentimentClass)
                {
                    continue;
                }
            }
            else
            {
                row = new CallTranscript { Id = Guid.NewGuid(), SiteId = siteId, CallId = callId, TalkId = transcript.Id, Lines = "[]", Text = "" };
                db.CallTranscripts.Add(row);
                added.Add((callId, row));
            }

            (row.TalkId, row.Summary, row.Sentiment, row.SentimentClass) = (transcript.Id, transcript.Summary, transcript.Sentiment, transcript.SentimentClass);
            (row.Lines, row.Text, row.CopiedAt) = (lines, text, now);
            changed++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (added, changed);
    }
}

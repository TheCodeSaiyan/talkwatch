using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;

namespace TalkWatch.Data;

public sealed record IngestResult(int PagesRead, int CallsAdded, int CallsUpdated);

/// <summary>
/// Copies Talk's call log into TalkWatch's own database.
/// </summary>
/// <remarks>
/// Reads newest first and stops at the first full page that brings nothing new, so a routine poll costs one request
/// and the first run walks the whole history. Calls already stored are updated from every page read, because a
/// recent call can still change (an ongoing call ends; a recording is attached). This does not depend on the unit of
/// Talk's time filters, which the spike could not confirm.
/// </remarks>
public sealed class CallLogIngestor(
    TalkClient? talk, TalkWatchDbContext db, Guid siteId, NumberNormaliser numbers, TimeProvider clock, LineDirectory? directory = null,
    Func<IReadOnlyList<CallLogRecord>, CancellationToken, Task>? afterUpsert = null)
{
    /// <summary>What a missing direction or status is stored as.</summary>
    public const string Unknown = "unknown";

    public int PageSize { get; init; } = 100;

    /// <summary>
    /// Calls older than this are not stored: the retention policy has removed them, and the console still has them.
    /// Null stores everything.
    /// </summary>
    public DateTimeOffset? KeepSince { get; init; }

    /// <summary>A ceiling on one run, so a console with unexpected paging cannot keep TalkWatch reading for ever.</summary>
    public int MaxPages { get; init; } = 10_000;

    public async Task<IngestResult> IngestAsync(CancellationToken cancellationToken)
    {
        int pages = 0, added = 0, updated = 0;
        for (var pageNumber = 0; pageNumber < MaxPages; pageNumber++)
        {
            // A page that does not parse throws TalkSchemaException before anything from it is written.
            var page = await (talk ?? throw new InvalidOperationException("Paging the call log needs a Talk client.")).GetCallLogPageAsync(pageNumber, PageSize, cancellationToken);
            pages++;
            if (page.Records.Count == 0)
            {
                break;
            }

            var (newOnPage, updatedOnPage) = await UpsertAsync(page.Records, "call_log", page.RawJson, cancellationToken);
            added += newOnPage;
            updated += updatedOnPage;

            if (newOnPage == 0 || page.Records.Count < PageSize)
            {
                break;
            }
        }

        return new IngestResult(pages, added, updated);
    }

    /// <summary>
    /// Stores call records however they arrived: a page of the call log, or a live CALL_LOG_UPDATED message. New calls
    /// are added and known ones updated, and the payload is archived exactly as Talk sent it.
    /// </summary>
    public async Task<(int Added, int Updated)> UpsertAsync(IReadOnlyList<CallLogRecord> records, string endpoint, string rawJson, CancellationToken cancellationToken)
    {
        // A response holding a call older than the policy keeps is not archived either: it would keep that call.
        var archive = KeepSince is not { } keepSince || records.All(r => r.Time >= keepSince);
        if (KeepSince is { } since)
        {
            records = [.. records.Where(r => r.Time >= since)];
        }

        var uuids = records.Select(r => r.Uuid).ToList();
        var known = await db.Calls
            .Where(c => c.SiteId == siteId && uuids.Contains(c.TalkUuid))
            .Include(c => c.Events)
            .Include(c => c.Lines)
            .ToDictionaryAsync(c => c.TalkUuid, cancellationToken);

        // What an answering line's transcript showed about a call outlives this re-read: it is laid over the outcome again.
        var knownIds = known.Values.Select(c => c.Id).ToList();
        var findings = await db.CallFindings.IgnoreQueryFilters().Where(f => f.SiteId == siteId && knownIds.Contains(f.CallId))
            .ToDictionaryAsync(f => f.CallId, f => f.Finding, cancellationToken);

        var now = clock.GetUtcNow();
        int added = 0, updated = 0;
        foreach (var record in records)
        {
            if (known.TryGetValue(record.Uuid, out var existing))
            {
                if (Apply(existing, record, now, findings.TryGetValue(existing.Id, out var found) ? found : null))
                {
                    updated++;
                }
            }
            else
            {
                var call = new CallRow { Id = Guid.NewGuid(), SiteId = siteId, TalkUuid = record.Uuid, Direction = Unknown, Status = Unknown, IngestedAt = now };
                Apply(call, record, now);
                db.Calls.Add(call);
                known[record.Uuid] = call;
                added++;
            }
        }

        if (archive)
        {
            db.RawPayloads.Add(new RawPayload { Id = Guid.NewGuid(), SiteId = siteId, Endpoint = endpoint, FetchedAt = now, Body = rawJson });
        }
        await db.SaveChangesAsync(cancellationToken);

        // A new outbound call, or an answered one from someone who had been missed, returns their earlier missed calls.
        await CallBacks.MarkReturnedAsync(db, siteId, cancellationToken);

        // After the calls are safely stored: alerting looks at them, and a failure there never loses a call.
        if (afterUpsert is not null)
        {
            await afterUpsert(records, cancellationToken);
        }

        return (added, updated);
    }

    /// <summary>
    /// Calls still in progress whose hang-up the console never recorded: ringing or in a menu with nothing new for
    /// <see cref="CallOutcomes.RingingOver"/>, or started a day ago. Their outcome is decided now from what they have,
    /// so they stop showing as live. Returns how many were settled.
    /// </summary>
    public async Task<int> SettleAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var before = now - CallOutcomes.RingingOver;
        var stale = (await db.Calls.Include(c => c.Events)
                .Where(c => c.SiteId == siteId && c.Outcome == CallOutcome.InProgress && c.Time < before)
                .ToListAsync(cancellationToken))
            .Where(c => CallOutcomes.Over(c.Time, c.Events.Count == 0 ? null : c.Events.Max(e => e.Time), now))
            .ToList();
        foreach (var call in stale)
        {
            call.Outcome = CallOutcomes.Of(call.Direction, call.Status, [.. call.Events.Select(e => e.Event)], longOver: true);
            call.UpdatedAt = clock.GetUtcNow();
        }

        await db.SaveChangesAsync(cancellationToken);
        return stale.Count;
    }

    /// <summary>
    /// Brings a call still in progress up to date with its events as Talk has them now (a key pressed, an option
    /// entered, who is ringing), read when Talk announces CALL_EVENTS_UPDATED. The call record itself arrives with
    /// CALL_LOG_UPDATED, which can come a moment later, so a call not stored yet is left for it. A call that has ended
    /// is left alone: its record is the whole story. Returns whether anything changed.
    /// </summary>
    public async Task<bool> UpdateEventsAsync(string callUuid, IReadOnlyList<CallEvent> events, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(events);
        var call = await db.Calls.Include(c => c.Events).SingleOrDefaultAsync(c => c.SiteId == siteId && c.TalkUuid == callUuid, cancellationToken);
        if (call is null || call.Outcome != CallOutcome.InProgress || !ReplaceEvents(call, events))
        {
            return false;
        }

        var now = clock.GetUtcNow();
        var lastEvent = events.Count == 0 ? (DateTimeOffset?)null : events.Max(e => e.Time);
        call.Outcome = CallOutcomes.Of(call.Direction, call.Status, [.. events.Select(e => e.Event)], CallOutcomes.Over(call.Time, lastEvent, now));
        call.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Replaces a call's events when Talk's differ from those stored. Returns whether they did.</summary>
    private bool ReplaceEvents(CallRow call, IReadOnlyList<CallEvent> callEvents)
    {
        var events = callEvents.Select((e, i) => (e, i)).ToList();
        var changed = call.Events.Count != events.Count
            || call.Events.OrderBy(e => e.Sequence).Zip(events).Any(p => p.First.EventUuid != p.Second.e.EventUuid || p.First.Event != p.Second.e.Event);
        if (!changed)
        {
            return false;
        }

        call.Events.Clear();
        call.Events.AddRange(events.Select(p => new CallEventRow
        {
            Id = Guid.NewGuid(),
            SiteId = siteId,
            CallId = call.Id,
            Sequence = p.i,
            Time = p.e.Time,
            Event = p.e.Event,
            EventUuid = p.e.EventUuid,
            DataJson = p.e.EventData?.GetRawText(),
        }));
        return true;
    }

    /// <summary>Copies a record onto a call. Returns true when anything changed.</summary>
    private bool Apply(CallRow call, CallLogRecord record, DateTimeOffset now, OutsideFinding? finding = null)
    {
        var before = Fingerprint(call);

        call.Time = record.Time;
        call.Direction = record.Direction ?? Unknown;
        call.Status = record.Status ?? Unknown;
        call.DurationSeconds = record.Duration ?? 0;
        call.FromRaw = record.From;
        call.FromE164 = numbers.ToE164(record.From);
        call.ToRaw = record.To;
        call.ToE164 = numbers.ToE164(record.To);
        call.AnsweredByRaw = record.AnsweredBy;
        call.AnsweredByE164 = numbers.ToE164(record.AnsweredBy);
        call.CallerName = record.FromCallerName;
        call.HasRecording = record.Recording == true;
        call.RecordingFilename = record.RecordingFilename;
        call.Country = record.Country;
        call.QualityScore = record.QualityScore;

        var eventsChanged = ReplaceEvents(call, record.CallEvents);

        var lines = CallRouting.TouchedLines(record, numbers, directory ?? LineDirectory.Empty);
        var linesChanged = !lines.SetEquals(call.Lines.Select(l => new LineRef(l.Kind, l.Key)));
        if (linesChanged)
        {
            call.Lines.Clear();
            call.Lines.AddRange(lines.Select(l => new CallLine { SiteId = siteId, CallId = call.Id, Kind = l.Kind, Key = l.Key }));
        }

        var lastEvent = record.CallEvents.Count == 0 ? (DateTimeOffset?)null : record.CallEvents.Max(e => e.Time);
        call.Outcome = CallOutcomes.Of(record.Direction, record.Status, [.. record.CallEvents.Select(e => e.Event)], CallOutcomes.Over(record.Time, lastEvent, now)).With(finding);

        var changed = eventsChanged || linesChanged || before != Fingerprint(call);
        if (changed)
        {
            call.UpdatedAt = now;
        }

        return changed;
    }

    private static string Fingerprint(CallRow c) =>
        string.Join('|', c.Time.ToUnixTimeMilliseconds(), c.Direction, c.Status, c.Outcome, c.DurationSeconds, c.FromRaw, c.ToRaw, c.AnsweredByRaw,
            c.CallerName, c.HasRecording, c.RecordingFilename, c.Country, c.QualityScore);
}

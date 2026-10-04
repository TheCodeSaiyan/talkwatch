using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Web.Components.Alerts;

namespace TalkWatch.Web.Services;

/// <summary>
/// Who answered calls in the period, and how: a Talk user, or the number that picked up (an outside mobile a call was
/// forwarded to, by its contact's name); with the calls they made and the voicemail left for them.
/// </summary>
public sealed record PersonRow(string Name, int Answered, TimeSpan? AverageTalk, TimeSpan? MedianPickUp, int CallsOut, int Voicemails);

/// <summary>A missed call or voicemail in the period, and whether the caller has been got back to.</summary>
public sealed record MissedRow(DateTimeOffset Time, string Caller, string Line, TimeSpan? Rang, bool Voicemail, bool Outside, DateTimeOffset? ReturnedAt, CallBackHow? ReturnedHow);

/// <summary>One caller in the period: how often they rang, how often they got through, and whether they had rung before.</summary>
public sealed record CallerRow(string Who, int Calls, int GotThrough, bool New);

public sealed record CallerSummary(int Callers, int Repeat, int New, IReadOnlyList<CallerRow> Top);

/// <summary>How Talk's transcription rated the period's calls, and the ones it rated negative.</summary>
public sealed record SentimentSummary(int Positive, int Neutral, int Negative, IReadOnlyList<(DateTimeOffset Time, string Who)> NegativeCalls);

/// <summary>How quickly the period's missed callers were got back to, and how.</summary>
public sealed record CallBackTimes(int Returned, int Waiting, TimeSpan? Median, int WithinHour, IReadOnlyList<(DateTimeOffset Time, string Who, TimeSpan After)> Slowest,
    IReadOnlyList<(CallBackHow How, int Count)> ByHow);

/// <summary>An outside answering line's voicemail in the period: messages left there, and callers who hung up at its greeting.</summary>
public sealed record OutsideRow(string Contact, int MessagesLeft, int HungUp);

/// <summary>The alerts raised in the period: by kind, how many were acknowledged, how fast, and by whom.</summary>
public sealed record AlertSummary(int Raised, int Acknowledged, TimeSpan? MedianToAcknowledge, IReadOnlyList<(string Kind, int Count)> ByKind,
    IReadOnlyList<(string Who, int Count)> ByWho);

/// <summary>
/// The report's further parts, each worked out only when the report holds it, through its reader's access like the rest:
/// the database filters decide which calls, transcripts and alerts each copy counts.
/// </summary>
public sealed record ReportParts(
    IReadOnlyList<PersonRow>? People, IReadOnlyList<MissedRow>? Missed, int MissedTotal, CallerSummary? Callers, SentimentSummary? Sentiment,
    CallBackTimes? CallBacks, IReadOnlyList<OutsideRow>? Outside, AlertSummary? Alerts)
{
    /// <summary>The most missed calls a report lists; the rest are counted, and the call list has every one.</summary>
    public const int MissedShown = 200;

    /// <summary>The most callers a report lists.</summary>
    public const int CallersShown = 15;

    public static async Task<ReportParts> ComputeAsync(TalkWatchDbContext db, ReportSections sections, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone,
        IReadOnlyList<Core.Talk.TalkContact> contacts, TimeWindow? hours, CancellationToken cancellationToken)
    {
        var (start, end) = (from.ToUniversalTime(), to.ToUniversalTime());
        var inbound = db.Calls.AsNoTracking().Where(c => c.Direction == "in" && c.Time >= start && c.Time < end);
        var needNames = sections.HasFlag(ReportSections.MissedList) || sections.HasFlag(ReportSections.Callers) || sections.HasFlag(ReportSections.Sentiment)
            || sections.HasFlag(ReportSections.CallBackTimes);
        var names = needNames
            ? await CallerNames.ForAsync(db, await inbound.Select(c => c.FromE164).Distinct().ToListAsync(cancellationToken), cancellationToken)
            : new Dictionary<string, string>();

        IReadOnlyList<MissedRow>? missed = null;
        var missedTotal = 0;
        if (sections.HasFlag(ReportSections.MissedList))
        {
            var rows = await inbound.Where(c => CallOutcomes.UnansweredKinds.Contains(c.Outcome)).OrderBy(c => c.Time)
                .Select(c => new { c.Id, c.Time, c.CallerName, c.FromRaw, c.FromE164, c.ToE164, c.ToRaw, c.Outcome, c.ReturnedAt, c.ReturnedHow }).ToListAsync(cancellationToken);
            missedTotal = rows.Count;
            var shown = rows.Take(MissedShown).ToList();
            var ids = shown.Select(r => r.Id).ToList();
            var rang = await GaveUpAfterAsync(db, ids, cancellationToken);
            var lines = await db.Lines.AsNoTracking().Where(l => l.Kind == LineKind.Did).ToDictionaryAsync(l => l.Key, l => l.Name, cancellationToken);
            missed = [.. shown.Select(r => new MissedRow(r.Time, CallerNames.Who(r.CallerName, r.FromRaw, r.FromE164, names),
                r.ToE164 is { } did && lines.GetValueOrDefault(did) is { Length: > 0 } name ? name : r.ToRaw ?? "",
                rang.GetValueOrDefault(r.Id), r.Outcome.IsVoicemail(), r.Outcome is CallOutcome.OutsideVoicemail or CallOutcome.OutsideMissed, r.ReturnedAt, r.ReturnedHow))];
        }

        IReadOnlyList<PersonRow>? people = null;
        if (sections.HasFlag(ReportSections.People))
        {
            // Who answered: the Talk user when Talk names them, else the number that picked up (often an outside mobile a
            // call was forwarded to), named from the directory and contacts. Calls out go to the user who made them, and a
            // voicemail to whose box it went to.
            var userNames = await db.Lines.AsNoTracking().Where(l => l.Kind == LineKind.User).ToDictionaryAsync(l => l.Key, l => l.Name, cancellationToken);
            var calls = await db.Calls.AsNoTracking().Where(c => c.Time >= start && c.Time < end
                    && ((c.Direction == "in" && (c.Outcome == CallOutcome.Answered || CallOutcomes.VoicemailKinds.Contains(c.Outcome))) || c.Direction == "out"))
                .Select(c => new
                {
                    c.Id, c.Direction, c.Outcome, c.DurationSeconds, c.AnsweredByE164, c.AnsweredByRaw,
                    Users = c.Lines.Where(l => l.Kind == LineKind.User).Select(l => l.Key).ToList(),
                })
                .ToListAsync(cancellationToken);
            var answererNames = await CallerNames.ForAsync(db, calls.Select(c => c.AnsweredByE164).Distinct(), cancellationToken);
            string User(string key) => userNames.GetValueOrDefault(key) is { Length: > 0 } n ? n : "Someone no longer in Talk";
            string? Answerer(string? e164, string? raw, List<string> users) =>
                users.Count > 0 ? User(users[0])
                : e164 is not null ? CallerNames.Who(null, raw, e164, answererNames)
                : raw is { Length: > 0 } ? raw : null;

            var answered = calls.Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered)
                .Select(c => (c.Id, c.DurationSeconds, Who: Answerer(c.AnsweredByE164, c.AnsweredByRaw, c.Users))).Where(c => c.Who is not null).ToList();
            var pickUp = await PickUpAsync(db, [.. answered.Select(a => a.Id)], cancellationToken);
            var outbound = calls.Where(c => c.Direction == "out").SelectMany(c => c.Users.Take(1).Select(User)).ToList();
            var voicemail = calls.Where(c => c.Direction == "in" && c.Outcome.IsVoicemail()).SelectMany(c => c.Users.Select(User)).ToList();
            people = [.. answered.Select(a => a.Who!).Concat(outbound).Concat(voicemail).Distinct()
                .Select(name =>
                {
                    var took = answered.Where(a => a.Who == name).ToList();
                    var waits = took.Select(t => pickUp.GetValueOrDefault(t.Id)).OfType<TimeSpan>().Order().ToList();
                    return new PersonRow(name, took.Count, took.Count == 0 ? null : TimeSpan.FromSeconds(took.Average(t => t.DurationSeconds)), Median(waits),
                        outbound.Count(o => o == name), voicemail.Count(v => v == name));
                })
                .OrderByDescending(p => p.Answered).ThenByDescending(p => p.CallsOut).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        }

        CallerSummary? callers = null;
        if (sections.HasFlag(ReportSections.Callers))
        {
            var calls = await inbound.Where(c => c.FromE164 != null).Select(c => new { c.FromE164, c.FromRaw, c.CallerName, c.Outcome }).ToListAsync(cancellationToken);
            var numbers = calls.Select(c => c.FromE164!).Distinct().ToList();
            var before = (await db.Calls.AsNoTracking().Where(c => c.Direction == "in" && c.Time < start && c.FromE164 != null && numbers.Contains(c.FromE164))
                .Select(c => c.FromE164!).Distinct().ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            var byCaller = calls.GroupBy(c => c.FromE164!).Select(g => new CallerRow(
                CallerNames.Who(g.Select(c => c.CallerName).FirstOrDefault(n => !string.IsNullOrEmpty(n)), g.First().FromRaw, g.Key, names),
                g.Count(), g.Count(c => c.Outcome == CallOutcome.Answered), !before.Contains(g.Key))).ToList();
            callers = new CallerSummary(byCaller.Count, byCaller.Count(c => c.Calls > 1), byCaller.Count(c => c.New),
                [.. byCaller.OrderByDescending(c => c.Calls).ThenBy(c => c.Who, StringComparer.CurrentCultureIgnoreCase).Take(CallersShown)]);
        }

        SentimentSummary? sentiment = null;
        if (sections.HasFlag(ReportSections.Sentiment))
        {
            // Through the transcripts' own filter: a reader not allowed transcripts counts none.
            var rated = await (
                    from t in db.CallTranscripts
                    join c in db.Calls on t.CallId equals c.Id
                    where c.Time >= start && c.Time < end && t.SentimentClass != null
                    select new { t.SentimentClass, c.Time, c.Direction, c.CallerName, c.FromRaw, c.FromE164, c.ToRaw })
                .ToListAsync(cancellationToken);
            sentiment = new SentimentSummary(
                rated.Count(r => r.SentimentClass == "positive"), rated.Count(r => r.SentimentClass == "neutral"), rated.Count(r => r.SentimentClass == "negative"),
                [.. rated.Where(r => r.SentimentClass == "negative").OrderBy(r => r.Time)
                    .Select(r => (r.Time, r.Direction == "out" ? $"To {r.ToRaw}" : CallerNames.Who(r.CallerName, r.FromRaw, r.FromE164, names)))]);
        }

        CallBackTimes? callBacks = null;
        if (sections.HasFlag(ReportSections.CallBackTimes))
        {
            var returnable = await db.Calls.AsNoTracking().Returnable().Where(c => c.Time >= start && c.Time < end)
                .Select(c => new { c.Time, c.ReturnedAt, c.ReturnedHow, c.CallerName, c.FromRaw, c.FromE164 }).ToListAsync(cancellationToken);
            var back = returnable.Where(c => c.ReturnedAt is not null).Select(c => (c.Time, Who: CallerNames.Who(c.CallerName, c.FromRaw, c.FromE164, names),
                After: c.ReturnedAt!.Value - c.Time, c.ReturnedHow)).ToList();
            callBacks = new CallBackTimes(back.Count, returnable.Count - back.Count, Median([.. back.Select(b => b.After).Order()]),
                back.Count(b => b.After <= TimeSpan.FromHours(1)),
                [.. back.OrderByDescending(b => b.After).Take(3).Select(b => (b.Time, b.Who, b.After))],
                [.. back.Where(b => b.ReturnedHow is not null).GroupBy(b => b.ReturnedHow!.Value).Select(g => (g.Key, g.Count())).OrderByDescending(g => g.Item2)]);
        }

        IReadOnlyList<OutsideRow>? outside = null;
        if (sections.HasFlag(ReportSections.OutsideVoicemail))
        {
            var found = await (
                    from f in db.CallFindings
                    join c in db.Calls on f.CallId equals c.Id
                    where c.Time >= start && c.Time < end && (c.Outcome == CallOutcome.OutsideVoicemail || c.Outcome == CallOutcome.OutsideMissed)
                    select new { f.ContactId, c.Outcome })
                .ToListAsync(cancellationToken);
            outside = [.. found.GroupBy(f => f.ContactId ?? "").Select(g => new OutsideRow(
                    contacts.FirstOrDefault(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == g.Key)?.DisplayName ?? $"Contact {g.Key}",
                    g.Count(f => f.Outcome == CallOutcome.OutsideVoicemail), g.Count(f => f.Outcome == CallOutcome.OutsideMissed)))
                .OrderByDescending(r => r.MessagesLeft + r.HungUp)];
        }

        AlertSummary? alerts = null;
        if (sections.HasFlag(ReportSections.Alerts))
        {
            var raised = await db.AlertEvents.AsNoTracking().Where(e => e.At >= start && e.At < end)
                .Select(e => new { e.Type, e.At, e.AcknowledgedAt, e.AcknowledgedBy }).ToListAsync(cancellationToken);
            raised = [.. raised.Where(e => hours is null || hours.Matches(e.At, zone))];
            var acknowledged = raised.Where(e => e.AcknowledgedAt is not null).ToList();
            alerts = new AlertSummary(raised.Count, acknowledged.Count,
                Median([.. acknowledged.Select(e => e.AcknowledgedAt!.Value - e.At).Where(t => t >= TimeSpan.Zero).Order()]),
                [.. raised.GroupBy(e => e.Type).Select(g => (FlowText.EventName(g.Key), g.Count())).OrderByDescending(g => g.Item2)],
                [.. acknowledged.GroupBy(e => e.AcknowledgedBy ?? "someone").Select(g => (g.Key == "link" ? "From a notification" : g.Key, g.Count())).OrderByDescending(g => g.Item2)]);
        }

        return new ReportParts(people, missed, missedTotal, callers, sentiment, callBacks, outside, alerts);
    }

    private static TimeSpan? Median(List<TimeSpan> ordered) =>
        ordered.Count == 0 ? null : ordered.Count % 2 == 1 ? ordered[ordered.Count / 2] : (ordered[(ordered.Count / 2) - 1] + ordered[ordered.Count / 2]) / 2;

    // From the call starting to its hang-up: how long a missed caller waited before giving up.
    private static async Task<Dictionary<Guid, TimeSpan>> GaveUpAfterAsync(TalkWatchDbContext db, List<Guid> ids, CancellationToken cancellationToken) =>
        await Between(db, ids, "call_hangup", cancellationToken);

    // From the call starting to it being answered.
    private static async Task<Dictionary<Guid, TimeSpan>> PickUpAsync(TalkWatchDbContext db, List<Guid> ids, CancellationToken cancellationToken) =>
        await Between(db, ids, "call_accepted", cancellationToken);

    private static async Task<Dictionary<Guid, TimeSpan>> Between(TalkWatchDbContext db, List<Guid> ids, string to, CancellationToken cancellationToken)
    {
        var marks = await db.CallEvents.AsNoTracking().Where(e => ids.Contains(e.CallId) && (e.Event == "call_started" || e.Event == to))
            .Select(e => new { e.CallId, e.Event, e.Time }).ToListAsync(cancellationToken);
        return marks.GroupBy(m => m.CallId)
            .Select(g => (g.Key, Start: g.FirstOrDefault(m => m.Event == "call_started")?.Time, End: g.FirstOrDefault(m => m.Event == to)?.Time))
            .Where(x => x.Start is not null && x.End is not null && x.End >= x.Start)
            .ToDictionary(x => x.Key, x => x.End!.Value - x.Start!.Value);
    }
}

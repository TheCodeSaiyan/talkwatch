using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Calls forwarded to an outside answering line: when one's transcript arrives, whether a person took it or the
/// provider's voicemail did (<see cref="OutsideVoicemail"/>), and a person's correction of that. Both store the finding,
/// lay it over the call's outcome, and raise the alert the new outcome calls for, late and once: Voicemail, or Missed.
/// Nothing already sent is taken back.
/// </summary>
public sealed partial class AnsweringLineCheck(IServiceScopeFactory scopes, AlertService alerts, TimeProvider clock, ILogger<AnsweringLineCheck> logger)
{
    /// <summary>
    /// Reads a newly arrived transcript, if its call was answered by a ticked Contact and nobody has set its finding by
    /// hand. Returns whether the call's outcome changed.
    /// </summary>
    public async Task<bool> CheckAsync(Guid callId, IReadOnlyList<TranscriptLine> lines, TimeSpan recentEnough, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var call = await db.Calls.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == callId, cancellationToken);
        if (call is null || ContactOf(call) is not { } contact || call.Outcome is not (CallOutcome.Answered or CallOutcome.OutsideVoicemail or CallOutcome.OutsideMissed))
        {
            return false;
        }

        var line = await db.AnsweringLines.SingleOrDefaultAsync(a => a.ContactId == contact, cancellationToken);
        var existing = await db.CallFindings.SingleOrDefaultAsync(f => f.CallId == callId, cancellationToken);
        if (line is null || existing is { ByHand: true } || OutsideVoicemail.Judge(lines, AnsweringLine.PhrasesOf(line.Phrases)) is not { } judged)
        {
            return false;
        }

        LogJudged(logger, call.TalkUuid, judged.Finding);
        return await ApplyAsync(db, call, existing, judged.Finding, judged.Phrase, byHand: null, recentEnough, cancellationToken);
    }

    /// <summary>A person's correction: it stands over whatever the transcript said, now and on every later read.</summary>
    public async Task<bool> MarkAsync(Guid callId, OutsideFinding finding, string who, TimeSpan recentEnough, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var call = await db.Calls.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == callId, cancellationToken);
        if (call is null || ContactOf(call) is null)
        {
            return false;
        }

        var existing = await db.CallFindings.SingleOrDefaultAsync(f => f.CallId == callId, cancellationToken);
        return await ApplyAsync(db, call, existing, finding, null, who, recentEnough, cancellationToken);
    }

    /// <summary>What reprocessing a contact's calls found.</summary>
    public sealed record Reprocessed(int Calls, int MessageLeft, int HungUp, int Person, int TooShort, int NoTranscript, int ByHand);

    /// <summary>
    /// Reads every call the contact answered again, against its phrases as they are now: a phrase added can turn a past
    /// call into voicemail, one taken away can turn it back. A finding set by hand is left alone. Late alerts follow the
    /// usual rule: only for calls still recent, and never a second time.
    /// </summary>
    public async Task<Reprocessed> ReprocessAsync(string contactId, TimeSpan recentEnough, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        if (await db.AnsweringLines.AsNoTracking().SingleOrDefaultAsync(a => a.ContactId == contactId, cancellationToken) is not { } line)
        {
            return new Reprocessed(0, 0, 0, 0, 0, 0, 0);
        }

        var phrases = AnsweringLine.PhrasesOf(line.Phrases);
        var ids = await (
                from l in db.CallLines
                join c in db.Calls on l.CallId equals c.Id
                where l.Kind == LineKind.Contact && l.Key == contactId && c.Direction == "in"
                    && (c.Outcome == CallOutcome.Answered || c.Outcome == CallOutcome.OutsideVoicemail || c.Outcome == CallOutcome.OutsideMissed)
                orderby c.Time
                select c.Id)
            .ToListAsync(cancellationToken);

        int message = 0, hungUp = 0, person = 0, tooShort = 0, noTranscript = 0, byHand = 0;
        foreach (var id in ids)
        {
            var existing = await db.CallFindings.SingleOrDefaultAsync(f => f.CallId == id, cancellationToken);
            if (existing is { ByHand: true })
            {
                byHand++;
                continue;
            }

            if (await db.CallTranscripts.AsNoTracking().SingleOrDefaultAsync(t => t.CallId == id, cancellationToken) is not { } transcript)
            {
                noTranscript++;
                continue;
            }

            var call = await db.Calls.Include(c => c.Lines).SingleAsync(c => c.Id == id, cancellationToken);
            if (OutsideVoicemail.Judge(TranscriptStore.LinesOf(transcript), phrases) is not { } judged)
            {
                // Too short to tell now: whatever an earlier reading decided goes, and the call is as Talk has it.
                tooShort++;
                if (existing is not null)
                {
                    db.CallFindings.Remove(existing);
                    call.Outcome = call.Outcome.With(null);
                    call.UpdatedAt = clock.GetUtcNow();
                    await db.SaveChangesAsync(cancellationToken);
                }

                continue;
            }

            await ApplyAsync(db, call, existing, judged.Finding, judged.Phrase, byHand: null, recentEnough, cancellationToken);
            _ = judged.Finding switch
            {
                OutsideFinding.MessageLeft => message++,
                OutsideFinding.HungUpAtGreeting => hungUp++,
                _ => person++,
            };
        }

        return new Reprocessed(ids.Count, message, hungUp, person, tooShort, noTranscript, byHand);
    }

    /// <summary>The outside Contact that answered the call, as its lines have it; null when none did.</summary>
    public static string? ContactOf(CallRow call) => AnsweredBy(call.Lines.Where(l => l.Kind == LineKind.Contact).Select(l => l.Key));

    /// <summary>
    /// Of a call's contact lines, the one that answered: Talk names it by its numeric id (answered_by_contact_id). The
    /// others are contacts the number's forwarding could reach, named by their uuid, and none of them answered.
    /// </summary>
    public static string? AnsweredBy(IEnumerable<string> contactKeys) =>
        contactKeys.FirstOrDefault(k => int.TryParse(k, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));

    private async Task<bool> ApplyAsync(TalkWatchDbContext db, CallRow call, CallFinding? existing, OutsideFinding finding, string? phrase, string? byHand,
        TimeSpan recentEnough, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var finds = existing ?? db.CallFindings.Add(new CallFinding { CallId = call.Id, SiteId = call.SiteId }).Entity;
        (finds.Finding, finds.ContactId, finds.Phrase, finds.ByHand, finds.DecidedBy, finds.DecidedAt) = (finding, ContactOf(call), phrase, byHand is not null, byHand, now);

        var before = call.Outcome;
        call.Outcome = call.Outcome.With(finding);
        if (call.Outcome != before)
        {
            call.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);

        // The alert the outcome now calls for, once (it is keyed by call and kind), while the call is recent.
        if (call.Outcome != before && call.Outcome.IsVoicemail())
        {
            await alerts.RaiseCallLateAsync(call.Id, AlertEventType.Voicemail, recentEnough, cancellationToken);
        }
        else if (call.Outcome != before && call.Outcome.IsMissed())
        {
            await alerts.RaiseCallLateAsync(call.Id, AlertEventType.MissedCall, recentEnough, cancellationToken);
        }

        return call.Outcome != before;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Call {Call}, answered by an outside answering line, read from its transcript as {Finding}.")]
    private static partial void LogJudged(ILogger logger, string call, OutsideFinding finding);
}

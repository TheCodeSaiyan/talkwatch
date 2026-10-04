using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Turns what happens into alerts: records each event once, starts each flow it applies to, and queues a delivery for
/// each channel a flow's stage reaches that may hear of it. Sending is the dispatcher's job.
/// </summary>
public sealed partial class AlertService(
    IServiceScopeFactory scopes, IOptions<SiteOptions> siteOptions, TimeProvider clock, LiveStatus live, LineDirectorySync directory, ILogger<AlertService> logger)
{
    /// <summary>
    /// Calls older than this raise nothing. The first import brings in the whole history, and an alert for every old
    /// missed call would be noise at best.
    /// </summary>
    public static readonly TimeSpan RecentCall = TimeSpan.FromMinutes(30);

    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(siteOptions.Value.TimeZone);

    /// <summary>What <see cref="AlertEvent.AcknowledgedBy"/> holds when no person did it.</summary>
    public const string ByLink = "link", ByCallBack = "call back", ByLaterCall = "later call", ByAnswered = "answered";

    /// <summary>How an alert was acknowledged, in words that follow "Acknowledged".</summary>
    public static string AcknowledgedHow(string? by) => by switch
    {
        null => "",
        ByLink => "from a notification",
        ByCallBack => "by calling them back",
        ByLaterCall => "when a later call from them was answered",
        ByAnswered => "when someone else answered the call",
        _ => $"by {by}",
    };

    /// <summary>
    /// Call records just stored, from a poll or a live update. Safe to call with the same records again. Missed callers
    /// got back to since are acknowledged first, so a flow waiting on them stops.
    /// </summary>
    public async Task RaiseForCallsAsync(IReadOnlyList<CallLogRecord> records, CancellationToken cancellationToken)
    {
        await AcknowledgeReturnedAsync(cancellationToken);
        await RaisePoorQualityAsync(records, cancellationToken);
        var now = clock.GetUtcNow();
        var recent = records.Where(r => now - r.Time <= RecentCall && CallAlerts.For(r.Direction, r.Status, [.. r.CallEvents.Select(e => e.Event)]).Count > 0).ToList();
        if (recent.Count == 0)
        {
            return;
        }

        var answered = new List<(Guid Alert, IReadOnlyCollection<Guid> Carriers)>();
        await WithDbAsync(async (db, site) =>
        {
            var uuids = recent.Select(r => r.Uuid).ToList();
            var calls = await db.Calls.Include(c => c.Lines).Include(c => c.Events).Where(c => uuids.Contains(c.TalkUuid)).ToListAsync(cancellationToken);
            var names = await CallerNames.ForAsync(db, calls.Select(c => c.FromE164), cancellationToken);
            foreach (var call in calls)
            {
                foreach (var type in CallAlerts.For(call.Direction, call.Status, [.. call.Events.Select(e => e.Event)]))
                {
                    await RaiseCallAsync(db, site, call, type, names, cancellationToken);
                }

                await RaiseForwardedAsync(db, site, call, names, cancellationToken);
                if (await AnsweredElsewhereAsync(db, call, cancellationToken) is { } done)
                {
                    answered.Add(done);
                }
            }
        }, cancellationToken);

        foreach (var (alert, carriers) in answered)
        {
            await AcknowledgeAsync(alert, ByAnswered, cancellationToken, carriers);
        }
    }

    /// <summary>
    /// A call put through outside that has since been answered: its alert is in hand, so whoever carries an outside phone
    /// it reached hears that someone answered, and need not ring the caller back. Those who carry the phone that answered
    /// are not told, since it was theirs. A phone ticked as an answering line is not taken as answering, because its
    /// voicemail answers too and that is only known once the transcript is read. Null when there is nothing to acknowledge.
    /// </summary>
    private static async Task<(Guid Alert, IReadOnlyCollection<Guid> Carriers)?> AnsweredElsewhereAsync(TalkWatchDbContext db, CallRow call, CancellationToken cancellationToken)
    {
        var (answered, contactUuid, contactId) = CallRouting.WhoAnswered(call.Events.OrderBy(e => e.Sequence).Select(e => (e.Event, e.DataJson)));
        if (!answered || (contactId is not null && await db.AnsweringLines.AnyAsync(a => a.ContactId == contactId, cancellationToken)))
        {
            return null;
        }

        var key = $"call:{call.TalkUuid}:{ForwardedKey}";
        if (await db.AlertEvents.Where(e => e.Key == key && e.AcknowledgedAt == null).Select(e => (Guid?)e.Id).SingleOrDefaultAsync(cancellationToken) is not { } alert)
        {
            return null;
        }

        var carriers = contactUuid is null ? [] : await db.ContactLinks.Where(l => l.ContactUuid == contactUuid).Select(l => l.UserId).ToListAsync(cancellationToken);
        return (alert, carriers);
    }

    /// <summary>
    /// An incoming call Talk has put through to one of its outside contacts, once per call: for the flows that wait for
    /// that (<see cref="Flows.WaitsForForward"/>). Talk reports the outside phone ringing while the call goes on, so this is
    /// raised then; a call only seen once it has ended (by the poll, after the live feed dropped) still raises it, saying so.
    /// </summary>
    private async Task RaiseForwardedAsync(TalkWatchDbContext db, Guid site, CallRow call, IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken)
    {
        if (call.Outcome is CallOutcome.Blocked || call.Direction != "in" || ContactsTried(call) is not { Count: > 0 } contacts)
        {
            return;
        }

        var when = TimeZoneInfo.ConvertTime(call.Time, Zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var who = CallerNames.Who(call.CallerName, call.FromRaw, call.FromE164, names);
        var to = string.Join(" or ", contacts.Select(c => directory.Current.Contacts.FirstOrDefault(k => k.Uuid == c)?.DisplayName ?? "an outside contact").Distinct());
        await RaiseAsync(db, site, new AlertEvent
        {
            Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.InboundCall, Key = $"call:{call.TalkUuid}:{ForwardedKey}", At = call.Time,
            CallId = call.Id, Title = "Call put through outside",
            Message = $"From {who} to {call.ToRaw} at {when}, put through to {to}{(Ended(call) ? ". The call has ended" : "")}",
        }, call.Lines.Select(l => new LineRef(l.Kind, l.Key)).ToHashSet(), call, cancellationToken, forwarded: true);
    }

    /// <summary>The last part of the key of the alert raised when a call is put through outside.</summary>
    public const string ForwardedKey = "ForwardedOutside";

    /// <summary>Whether a call is over: hung up, or ended unanswered, rather than ringing or being talked on.</summary>
    private static bool Ended(CallRow call) =>
        call.Events.Any(e => e.Event == "call_hangup") || call.Outcome.IsUnanswered() || call.Outcome == CallOutcome.HungUpAtSwitchboard;

    /// <summary>The outside contacts Talk put a call through to, by uuid (<see cref="CallRouting.ContactsTried"/>).</summary>
    private static IReadOnlyList<string> ContactsTried(CallRow call) =>
        CallRouting.ContactsTried(call.Events.OrderBy(e => e.Sequence).Select(e => (e.Event, e.DataJson)));

    /// <summary>
    /// A call's alert of one kind, once: keyed by the call and the kind, so raising it again changes nothing. For an
    /// outcome learnt after the call (an outside answering line's voicemail, read from the transcript), raised late,
    /// while the call is recent.
    /// </summary>
    public async Task RaiseCallLateAsync(Guid callId, AlertEventType type, TimeSpan recentEnough, CancellationToken cancellationToken) => await WithDbAsync(async (db, site) =>
    {
        var call = await db.Calls.Include(c => c.Lines).Include(c => c.Events).SingleOrDefaultAsync(c => c.Id == callId, cancellationToken);
        if (call is null || clock.GetUtcNow() - call.Time > recentEnough)
        {
            return;
        }

        await RaiseCallAsync(db, site, call, type, await CallerNames.ForAsync(db, [call.FromE164], cancellationToken), cancellationToken);
    }, cancellationToken);

    private async Task RaiseCallAsync(TalkWatchDbContext db, Guid site, CallRow call, AlertEventType type, IReadOnlyDictionary<string, string> names, CancellationToken cancellationToken)
    {
        var when = TimeZoneInfo.ConvertTime(call.Time, Zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var who = CallerNames.Who(call.CallerName, call.FromRaw, call.FromE164, names);
        var (title, message) = type switch
        {
            AlertEventType.MissedCall => ("Missed call", $"From {who} to {call.ToRaw} at {when}"),
            AlertEventType.Voicemail => ("New voicemail", $"From {who} to {call.ToRaw} at {when}"),
            AlertEventType.HungUpAtSwitchboard => ("Hung up at the switchboard", $"From {who} to {call.ToRaw} at {when}"),
            _ => ("Incoming call", $"From {who} to {call.ToRaw} at {when}"),
        };

        await RaiseAsync(db, site, new AlertEvent
        {
            Id = Guid.NewGuid(), SiteId = site, Type = type, Key = $"call:{call.TalkUuid}:{type}", At = call.Time,
            CallId = call.Id, Title = title, Message = message,
        }, call.Lines.Select(l => new LineRef(l.Kind, l.Key)).ToHashSet(), call, cancellationToken);
    }

    /// <summary>
    /// A call Talk's transcription rated negative, once, while the call is recent. The alert says which call, never
    /// what was said: a channel's grants are checked for the call's lines, not for transcripts.
    /// </summary>
    public async Task RaiseNegativeCallAsync(Guid callId, TimeSpan recentEnough, CancellationToken cancellationToken) => await WithDbAsync(async (db, site) =>
    {
        var call = await db.Calls.Include(c => c.Lines).SingleOrDefaultAsync(c => c.Id == callId, cancellationToken);
        if (call is null || clock.GetUtcNow() - call.Time > recentEnough)
        {
            return;
        }

        var when = TimeZoneInfo.ConvertTime(call.Time, Zone).ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var names = await CallerNames.ForAsync(db, [call.FromE164], cancellationToken);
        var who = call.Direction == "out" ? $"To {call.ToRaw}" : $"From {CallerNames.Who(call.CallerName, call.FromRaw, call.FromE164, names)} to {call.ToRaw}";
        await RaiseAsync(db, site, new AlertEvent
        {
            Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.NegativeCall, Key = $"call:{call.TalkUuid}:{AlertEventType.NegativeCall}", At = call.Time,
            CallId = call.Id, Title = "Call rated negative", Message = $"{who} at {when}. Talk's transcription rated it negative.",
        }, [.. call.Lines.Select(l => new LineRef(l.Kind, l.Key))], call, cancellationToken);
    }, cancellationToken);

    /// <summary>
    /// A voicemail's transcript has arrived, while the call is recent: once per call. The transcript is given to the flows
    /// for a words condition and goes no further; the alert says which call, never what was said.
    /// </summary>
    public async Task RaiseVoicemailTranscribedAsync(Guid callId, string transcript, TimeSpan recentEnough, CancellationToken cancellationToken) => await WithDbAsync(async (db, site) =>
    {
        var call = await db.Calls.Include(c => c.Lines).Include(c => c.Events).SingleOrDefaultAsync(c => c.Id == callId, cancellationToken);
        if (call is null || !call.Outcome.IsVoicemail() || clock.GetUtcNow() - call.Time > recentEnough)
        {
            return;
        }

        var when = TimeZoneInfo.ConvertTime(call.Time, Zone).ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        var names = await CallerNames.ForAsync(db, [call.FromE164], cancellationToken);
        await RaiseAsync(db, site, new AlertEvent
        {
            Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.VoicemailTranscribed, Key = $"call:{call.TalkUuid}:{AlertEventType.VoicemailTranscribed}", At = call.Time,
            CallId = call.Id, Title = "Voicemail transcribed",
            Message = $"From {CallerNames.Who(call.CallerName, call.FromRaw, call.FromE164, names)} to {call.ToRaw} at {when}. The transcript is on the call's page.",
        }, [.. call.Lines.Select(l => new LineRef(l.Kind, l.Key))], call, cancellationToken, transcript);
    }, cancellationToken);

    /// <summary>Something about a handset, alerted on once per minute it happens: reaches those granted the person it belongs to.</summary>
    public async Task RaiseHandsetAsync(LiveDevice device, AlertEventType type, string what, string title, string happened, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await WithDbAsync(async (db, site) =>
        {
            var name = device.Name ?? device.Model ?? device.Mac;
            HashSet<LineRef> lines = device.UserUuid is { } owner ? [new LineRef(LineKind.User, owner)] : [];
            await RaiseAsync(db, site, new AlertEvent
            {
                Id = Guid.NewGuid(), SiteId = site, Type = type, Key = $"device:{device.Mac}:{what}:{now:yyyyMMddHHmm}", At = now, UserUuid = device.UserUuid,
                Title = title, Message = $"{name}{(device.Ext is { Length: > 0 } ext ? $" (ext {ext})" : "")} {happened} at {TimeZoneInfo.ConvertTime(now, Zone):HH:mm}",
            }, lines, null, cancellationToken);
        }, cancellationToken);
    }

    /// <summary>Something about the whole site, such as the Talk account: on no line, so it reaches admins' and site channels.</summary>
    public async Task RaiseSiteAsync(AlertEventType type, string key, string title, string message, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await WithDbAsync(async (db, site) => await RaiseAsync(db, site, new AlertEvent
        {
            Id = Guid.NewGuid(), SiteId = site, Type = type, Key = key, At = now, Title = title, Message = message,
        }, [], null, cancellationToken), cancellationToken);
    }

    public async Task RaiseHandsetOfflineAsync(LiveDevice device, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await WithDbAsync(async (db, site) =>
        {
            var name = device.Name ?? device.Model ?? device.Mac;
            HashSet<LineRef> lines = device.UserUuid is { } owner ? [new LineRef(LineKind.User, owner)] : [];
            await RaiseAsync(db, site, new AlertEvent
            {
                Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.HandsetOffline,
                Key = $"device:{device.Mac}:offline:{now:yyyyMMddHHmm}", At = now, UserUuid = device.UserUuid,
                Title = "Handset offline", Message = $"{name}{(device.Ext is { Length: > 0 } ext ? $" (ext {ext})" : "")} went offline at {TimeZoneInfo.ConvertTime(now, Zone):HH:mm}",
            }, lines, null, cancellationToken);
        }, cancellationToken);
    }

    public async Task RaiseDriftAsync(string detail, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await WithDbAsync(async (db, site) => await RaiseAsync(db, site, new AlertEvent
        {
            Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.Drift, Key = $"drift:{now:yyyyMMddHH}", At = now,
            Title = "TalkWatch stopped copying calls",
            Message = "Talk's data has changed shape, probably after an update. Nothing is written until TalkWatch is updated. " + detail,
        }, [], null, cancellationToken), cancellationToken);
    }

    /// <param name="call">The call an alert is about, if it is about one: a flow's caller condition applies only then.</param>
    /// <param name="transcript">A voicemail's transcript, for a words condition: judged on, never stored with the alert.</param>
    /// <param name="forwarded">
    /// The alert raised when a call is put through outside: it runs only the incoming call flows that wait for that, and
    /// the alert raised when the call comes in runs only the others, so no flow runs twice for one call.
    /// </param>
    private async Task RaiseAsync(TalkWatchDbContext db, Guid site, AlertEvent alert, HashSet<LineRef> lines, CallRow? call, CancellationToken cancellationToken, string? transcript = null,
        bool forwarded = false)
    {
        if (await db.AlertEvents.AnyAsync(e => e.Key == alert.Key, cancellationToken))
        {
            return;
        }

        db.AlertEvents.Add(alert);
        var now = clock.GetUtcNow();
        var facts = await FactsAsync(db, alert.Type, alert.At, lines, call, transcript, cancellationToken);
        foreach (var flow in await db.AlertFlows.Where(f => f.Enabled && f.Trigger == alert.Type).ToListAsync(cancellationToken))
        {
            if (Flows.Read(flow.Definition) is not { } definition)
            {
                LogUnreadable(logger, flow.Name);
                continue;
            }

            if (alert.Type == AlertEventType.InboundCall && Flows.WaitsForForward(definition) != forwarded)
            {
                continue;
            }

            if (!Flows.Applies(definition, facts) || Flows.Plan(definition, facts) is not { Count: > 0 } plan)
            {
                continue;
            }

            var run = new AlertFlowRun
            {
                Id = Guid.NewGuid(), SiteId = site, EventId = alert.Id, FlowId = flow.Id, Plan = Flows.WritePlan(plan),
                DueAt = now + TimeSpan.FromMinutes(plan[0].WaitMinutes), State = FlowRunState.Running, CreatedAt = now,
            };
            db.AlertFlowRuns.Add(run);
            await AdvanceAsync(db, run, alert, plan, lines, now, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // The same event raised at the same moment from the poll and the live feed: the other one won.
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>
    /// What a flow is judged on for an alert: when, on which lines, and for a call who rang, how often, how long it rang,
    /// whether they are known or abroad, the call's quality, the menu options chosen, and a voicemail's words. One place,
    /// so a flow tried on past calls is judged exactly as it would be live.
    /// </summary>
    public async Task<FlowFacts> FactsAsync(TalkWatchDbContext db, AlertEventType type, DateTimeOffset at, HashSet<LineRef> lines, CallRow? call, string? transcript, CancellationToken cancellationToken)
    {
        var facts = new FlowFacts(type, at, lines, IsCall: call is not null, call?.FromE164, Zone) { TranscriptText = transcript };
        if (call?.FromE164 is { } caller)
        {
            // For a repeat caller condition: when this number rang in the day before, and this call, whether or not it is stored yet.
            var since = call.Time - TimeSpan.FromDays(1);
            var earlier = await db.Calls.AsNoTracking().Where(c => c.FromE164 == caller && c.Time > since && c.Time <= call.Time && c.Id != call.Id)
                .Select(c => c.Time).ToListAsync(cancellationToken);
            facts = facts with { CallerCalls = [.. earlier, call.Time] };
        }

        if (call is not null)
        {
            // The caller's week, for a caller-missed condition: this call and those before it.
            if (call.FromE164 is { } from)
            {
                var week = call.Time - TimeSpan.FromDays(7);
                var history = await db.Calls.AsNoTracking().Where(c => c.FromE164 == from && c.Time > week && c.Time <= call.Time && c.Id != call.Id)
                    .Select(c => new PastCall(c.Time, c.Outcome)).ToListAsync(cancellationToken);
                facts = facts with { CallerHistory = [.. history, new PastCall(call.Time, call.Outcome)] };
            }

            // The number it came in on, for the figures about that number: its day of calls, and its callers still waiting.
            if (lines.FirstOrDefault(l => l.Kind == LineKind.Did) is { Key: { } did })
            {
                var day = call.Time - TimeSpan.FromDays(1);
                var onNumber = await db.Calls.AsNoTracking()
                    .Where(c => c.Direction == "in" && c.Time > day && c.Time <= call.Time && c.Id != call.Id && c.Lines.Any(l => l.Kind == LineKind.Did && l.Key == did))
                    .Select(c => new PastCall(c.Time, c.Outcome)).ToListAsync(cancellationToken);
                var month = call.Time - TimeSpan.FromDays(Components.Pages.CallBacks.RecentDays);
                var waiting = await db.Calls.AsNoTracking().Returnable()
                    .Where(c => c.ReturnedAt == null && c.Time > month && c.Time <= call.Time && c.Lines.Any(l => l.Kind == LineKind.Did && l.Key == did))
                    .Select(c => c.FromE164).Distinct().CountAsync(cancellationToken);
                facts = facts with { NumberCalls = [.. onNumber, new PastCall(call.Time, call.Outcome)], WaitingCallers = waiting };
            }

            facts = facts with
            {
                RingSeconds = RingSeconds(call),
                CallerKnown = !string.IsNullOrWhiteSpace(call.CallerName)
                    || (call.FromE164 is { } number && (await CallerNames.ForAsync(db, [number], cancellationToken)).ContainsKey(number)),
                FromAbroad = NumberNormaliser.RegionOf(call.FromE164) is { } region ? !string.Equals(region, siteOptions.Value.Region, StringComparison.OrdinalIgnoreCase) : null,
                Quality = call.QualityScore,
                MenuChoices = MenuJourney.Of(call.Events.OrderBy(e => e.Sequence).Select(e => (e.Event, e.DataJson))).Choices.Select(c => c.ItemId).ToHashSet(),
                ForwardedOutside = ContactsTried(call).Count > 0,
            };
        }

        return facts;
    }

    /// <summary>One past call a flow would have run on, and what it would have done.</summary>
    public sealed record DryRunHit(CallRow Call, IReadOnlyList<FlowStage> Plan);

    /// <summary>What a flow would have done over past calls: how many raised its trigger, and those it would have run on.</summary>
    /// <param name="Why">Why the flow cannot be tried on past calls, when it cannot.</param>
    public sealed record DryRun(int Considered, IReadOnlyList<DryRunHit> Hits, string? Why = null);

    /// <summary>How many past calls a try looks at, at most: enough for a busy week, few enough to answer at once.</summary>
    public const int DryRunCalls = 500;

    /// <summary>
    /// What a flow, saved or not, would have done over the calls since <paramref name="since"/>, sending nothing: each call
    /// that raised the flow's trigger, judged on the same facts as live, and the plan the flow would have run for it.
    /// Through <paramref name="db"/>'s access scope, so a person tries it only on calls they can see.
    /// </summary>
    public async Task<DryRun> DryRunAsync(TalkWatchDbContext db, FlowDefinition flow, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (flow.Trigger is not (AlertEventType.InboundCall or AlertEventType.MissedCall or AlertEventType.Voicemail or AlertEventType.HungUpAtSwitchboard or AlertEventType.VoicemailTranscribed))
        {
            return new DryRun(0, [], "Only flows that start on an incoming call, a missed call, a voicemail, a transcribed voicemail or a hang-up in the menu can be tried on past calls.");
        }

        var calls = await db.Calls.AsNoTracking().Include(c => c.Lines).Include(c => c.Events)
            .Where(c => c.Time >= since && c.Direction == "in").OrderByDescending(c => c.Time).Take(DryRunCalls).ToListAsync(cancellationToken);
        var transcripts = flow.Trigger == AlertEventType.VoicemailTranscribed
            ? await db.CallTranscripts.AsNoTracking().Where(t => calls.Select(c => c.Id).Contains(t.CallId)).ToDictionaryAsync(t => t.CallId, t => t.Text, cancellationToken)
            : [];

        var considered = 0;
        var hits = new List<DryRunHit>();
        foreach (var call in calls)
        {
            var raises = flow.Trigger == AlertEventType.VoicemailTranscribed
                ? call.Outcome.IsVoicemail() && transcripts.ContainsKey(call.Id)
                : CallAlerts.For(call.Direction, call.Status, [.. call.Events.Select(e => e.Event)], call.Outcome).Contains(flow.Trigger);
            if (!raises)
            {
                continue;
            }

            considered++;
            var lines = call.Lines.Select(l => new LineRef(l.Kind, l.Key)).ToHashSet();
            var facts = await FactsAsync(db, flow.Trigger, call.Time, lines, call, transcripts.GetValueOrDefault(call.Id), cancellationToken);
            if (Flows.Applies(flow, facts) && Flows.Plan(flow, facts) is { Count: > 0 } plan)
            {
                hits.Add(new DryRunHit(call, plan));
            }
        }

        return new DryRun(considered, hits);
    }

    /// <summary>From the call starting to its being answered, sent to voicemail or hung up, whichever came first.</summary>
    private static double? RingSeconds(CallRow call)
    {
        var started = call.Events.FirstOrDefault(e => e.Event == "call_started")?.Time;
        var ended = call.Events.Where(e => e.Event is "call_accepted" or "call_sent_to_voicemail" or "call_hangup").OrderBy(e => e.Time).FirstOrDefault()?.Time;
        return started is { } s && ended is { } e && e >= s ? (e - s).TotalSeconds : null;
    }

    /// <summary>
    /// Runs whose next stage is due: each sends that stage, unless the alert has been acknowledged (the run stops), it is
    /// snoozed (the stage waits for the snooze to end), or its flow has been turned off (the run stops).
    /// </summary>
    public async Task AdvanceDueAsync(CancellationToken cancellationToken) => await WithDbAsync(async (db, _) =>
    {
        var now = clock.GetUtcNow();
        var due = await db.AlertFlowRuns.Where(r => r.State == FlowRunState.Running && r.DueAt <= now).OrderBy(r => r.DueAt).Take(50).ToListAsync(cancellationToken);
        foreach (var run in due)
        {
            var alert = await db.AlertEvents.SingleAsync(e => e.Id == run.EventId, cancellationToken);
            if (!await db.AlertFlows.AnyAsync(f => f.Id == run.FlowId && f.Enabled, cancellationToken))
            {
                Stop(run);
            }
            else
            {
                await AdvanceAsync(db, run, alert, Flows.ReadPlan(run.Plan), await LinesOfAsync(db, alert, cancellationToken), now, cancellationToken);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
            }
        }
    }, cancellationToken);

    /// <summary>Sends every stage of the run that is due by now, in order.</summary>
    private async Task AdvanceAsync(TalkWatchDbContext db, AlertFlowRun run, AlertEvent alert, IReadOnlyList<FlowStage> plan, HashSet<LineRef> lines, DateTimeOffset now, CancellationToken cancellationToken)
    {
        while (run.State == FlowRunState.Running && run.DueAt <= now)
        {
            if (alert.AcknowledgedAt is not null)
            {
                Stop(run);
                return;
            }

            if (alert.SnoozedUntil is { } snoozed && snoozed > run.DueAt)
            {
                run.DueAt = snoozed;
                continue;
            }

            await QueueStageAsync(db, run, alert, plan[run.NextStage], lines, now, cancellationToken);
            run.NextStage++;
            if (run.NextStage >= plan.Count)
            {
                (run.State, run.DueAt) = (FlowRunState.Done, null);
            }
            else
            {
                // A wait counts from when the stage before it was sent.
                run.DueAt = now + TimeSpan.FromMinutes(plan[run.NextStage].WaitMinutes);
            }
        }
    }

    /// <summary>
    /// The Talk users a "whoever is free" step reaches in a ring group: its members who are free now, from the live
    /// feed; when nobody is, or the live feed is not connected so nobody is known to be, every member.
    /// </summary>
    private List<string> FreeIn(string group) =>
        Reach(directory.Current.Groups.FirstOrDefault(g => g.Id == group)?.MemberList ?? [], live.Snapshot().Users, live.Connected);

    /// <summary>
    /// The channel kept for one of Talk's contacts, at the email Talk holds for them now: made the first time a flow
    /// reaches them, and its address and name brought up to date each time after. Empty when the contact is gone from
    /// Talk or has no email, so the step reaches nobody rather than an address Talk no longer holds.
    /// </summary>
    private async Task<Guid> ContactChannelAsync(TalkWatchDbContext db, Guid siteId, string uuid, CancellationToken cancellationToken)
    {
        var contact = directory.Current.Contacts.FirstOrDefault(c => c.Uuid == uuid);
        if (contact?.Address is not { } address)
        {
            return Guid.Empty;
        }

        var name = $"{contact.DisplayName} (contact)";
        var channel = await db.AlertChannels.FirstOrDefaultAsync(c => c.ContactUuid == uuid, cancellationToken);
        if (channel is null)
        {
            channel = new AlertChannel
            {
                Id = Guid.NewGuid(), SiteId = siteId, Name = name, Kind = ChannelKind.Email, Target = address, ContactUuid = uuid, CreatedAt = clock.GetUtcNow(),
            };
            db.AlertChannels.Add(channel);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (channel.Target != address || channel.Name != name || channel.Kind != ChannelKind.Email)
        {
            (channel.Target, channel.Name, channel.Kind) = (address, name, ChannelKind.Email);
            await db.SaveChangesAsync(cancellationToken);
        }

        return channel.Id;
    }

    /// <summary>Of a group's members, the free ones by the live feed; every member when none is, or the feed is not connected.</summary>
    public static List<string> Reach(IReadOnlyList<string> members, IEnumerable<LiveUser> users, bool connected)
    {
        var free = connected ? users.Where(u => u.State == "available" && members.Contains(u.Uuid, StringComparer.Ordinal)).Select(u => u.Uuid).ToList() : [];
        return free.Count > 0 ? free : [.. members];
    }

    private static void Stop(AlertFlowRun run) => (run.State, run.DueAt) = (FlowRunState.Stopped, null);

    /// <summary>
    /// One delivery per channel the stage reaches: a channel named in it, or a person's own channels. A channel owned by
    /// someone hears only of what they may see. A channel reached twice in a stage gets one delivery, urgent if either was.
    /// The one exception is the people behind an outside contact (<see cref="FlowRecipient.ToOutside"/>): they hear of a
    /// call put through to their phone whether or not they may see its line, but then only who is calling and on which line.
    /// </summary>
    private async Task QueueStageAsync(TalkWatchDbContext db, AlertFlowRun run, AlertEvent alert, FlowStage stage, HashSet<LineRef> lines, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var urgentByChannel = new Dictionary<Guid, bool>();
        var includeByChannel = new Dictionary<Guid, NotifyIncludes>();

        // Of the channels reached: those reached other than as someone behind an outside contact, and those whose owner may see the call.
        var reachedOtherwise = new HashSet<Guid>();
        var mayHear = new HashSet<Guid>();
        IReadOnlyList<string>? tried = null;
        foreach (var notify in stage.Notify)
        {
            foreach (var to in notify.To)
            {
                IQueryable<AlertChannel> channels;
                if (to.Outside is { } scope)
                {
                    // Never email, which would arrive long after the phone stopped ringing.
                    channels = db.AlertChannels.Where(c => c.Enabled && c.Kind != ChannelKind.Email);
                    if (scope == OutsideScope.ThisCall)
                    {
                        tried ??= alert.CallId is { } id && await db.Calls.AsNoTracking().Include(c => c.Events).SingleOrDefaultAsync(c => c.Id == id, cancellationToken) is { } forwardedCall
                            ? ContactsTried(forwardedCall)
                            : [];
                        var contacts = tried.ToList();
                        channels = channels.Where(c => db.ContactLinks.Any(l => l.UserId == c.OwnerUserId && contacts.Contains(l.ContactUuid)));
                    }
                    else
                    {
                        channels = channels.Where(c => db.ContactLinks.Any(l => l.UserId == c.OwnerUserId));
                    }

                    foreach (var channel in await channels.ToListAsync(cancellationToken))
                    {
                        if (await OwnerMayHearAsync(db, channel, alert, lines, cancellationToken))
                        {
                            mayHear.Add(channel.Id);
                        }

                        urgentByChannel[channel.Id] = urgentByChannel.GetValueOrDefault(channel.Id) || notify.Urgent;
                        includeByChannel[channel.Id] = includeByChannel.GetValueOrDefault(channel.Id) | notify.Include;
                    }

                    continue;
                }

                if (to.Channel is { } channelId)
                {
                    channels = db.AlertChannels.Where(c => c.Id == channelId && c.Enabled);
                }
                else if (to.Rang == true)
                {
                    // The Talk users the call touched: the person whose line it was, and everyone in a group it rang. Talk
                    // names a person only when they answered, it went to their voicemail or they were skipped; a missed call
                    // to a group names only the group, so its members come from the directory.
                    var rang = lines.Where(l => l.Kind == LineKind.User).Select(l => l.Key)
                        .Concat(directory.Current.Groups.Where(g => lines.Contains(new LineRef(LineKind.RingGroup, g.Id))).SelectMany(g => g.MemberList ?? []))
                        .Distinct(StringComparer.Ordinal).ToList();
                    channels = db.AlertChannels.Where(c => c.Enabled && db.Users.Any(u => u.Id == c.OwnerUserId && u.TalkUserUuid != null && rang.Contains(u.TalkUserUuid)));
                }
                else if (to.Contact is { } contact)
                {
                    var kept = await ContactChannelAsync(db, alert.SiteId, contact, cancellationToken);
                    channels = db.AlertChannels.Where(c => c.Id == kept && c.Enabled);
                }
                else if (to.FreeIn is { } group)
                {
                    var talkUsers = FreeIn(group);
                    channels = db.AlertChannels.Where(c => c.Enabled && db.Users.Any(u => u.Id == c.OwnerUserId && u.TalkUserUuid != null && talkUsers.Contains(u.TalkUserUuid)));
                }
                else
                {
                    channels = db.AlertChannels.Where(c => c.OwnerUserId == to.Person && c.Enabled);
                }

                foreach (var channel in await channels.ToListAsync(cancellationToken))
                {
                    if (await OwnerMayHearAsync(db, channel, alert, lines, cancellationToken))
                    {
                        reachedOtherwise.Add(channel.Id);
                        mayHear.Add(channel.Id);
                        urgentByChannel[channel.Id] = urgentByChannel.GetValueOrDefault(channel.Id) || notify.Urgent;
                        includeByChannel[channel.Id] = includeByChannel.GetValueOrDefault(channel.Id) | notify.Include;
                    }
                }
            }
        }

        // The call-back goes to the last person the stage names, unless the caller has been got back to already.
        if (stage.Assign.Count > 0 && alert.CallId is { } callId
            && await db.Calls.SingleOrDefaultAsync(c => c.Id == callId, cancellationToken) is { ReturnedAt: null } call)
        {
            (call.CallBackAssignedTo, call.CallBackAssignedAt) = (stage.Assign[^1], now);
        }

        // A bundled stage holds what it sends: the first alert for a channel opens a window, and the ones after it join it,
        // so all of them go together when it closes.
        var bundle = stage.BundleMinutes > 0 ? $"{run.FlowId:N}:{run.NextStage}" : null;
        foreach (var (channelId, urgent) in urgentByChannel)
        {
            // Reached only as someone behind an outside contact: sent at once, never bundled, and, when they may not see
            // the call, with who is calling and nothing more.
            var outside = !reachedOtherwise.Contains(channelId);
            var callerOnly = !mayHear.Contains(channelId);
            var at = now;
            if (bundle is not null && !outside)
            {
                var open = await db.AlertDeliveries.Where(d => d.State == DeliveryState.Pending && d.ChannelId == channelId && d.BundleKey == bundle && d.NextAttemptAt > now)
                    .OrderBy(d => d.NextAttemptAt).Select(d => (DateTimeOffset?)d.NextAttemptAt).FirstOrDefaultAsync(cancellationToken);
                at = open ?? now + TimeSpan.FromMinutes(stage.BundleMinutes);
            }

            db.AlertDeliveries.Add(new AlertDelivery
            {
                Id = Guid.NewGuid(), SiteId = run.SiteId, EventId = alert.Id, FlowId = run.FlowId, ChannelId = channelId,
                Stage = run.NextStage, Urgent = urgent, State = DeliveryState.Pending, NextAttemptAt = at, CreatedAt = now, BundleKey = outside ? null : bundle,
                Include = callerOnly ? (int)NotifyIncludes.None : (int)includeByChannel.GetValueOrDefault(channelId), Outside = outside, CallerOnly = callerOnly,
            });
        }
    }

    /// <summary>Marks an alert as in hand: nothing more is sent about it and it does not escalate. False if not found.</summary>
    /// <param name="notTelling">People whose channels are not sent word of it: they know already.</param>
    public async Task<bool> AcknowledgeAsync(Guid eventId, string by, CancellationToken cancellationToken, IReadOnlyCollection<Guid>? notTelling = null)
    {
        var found = false;
        await WithDbAsync(async (db, _) =>
        {
            var alert = await db.AlertEvents.SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);
            if (alert is null)
            {
                return;
            }

            found = true;
            if (alert.AcknowledgedAt is null)
            {
                alert.AcknowledgedAt = clock.GetUtcNow();
                alert.AcknowledgedBy = by.Length > 100 ? by[..100] : by;
                await db.AlertDeliveries.Where(d => d.EventId == eventId && d.State == DeliveryState.Pending)
                    .ExecuteUpdateAsync(u => u.SetProperty(d => d.State, DeliveryState.Cancelled), cancellationToken);
                await db.AlertFlowRuns.Where(r => r.EventId == eventId && r.State == FlowRunState.Running)
                    .ExecuteUpdateAsync(u => u.SetProperty(r => r.State, FlowRunState.Stopped).SetProperty(r => r.DueAt, (DateTimeOffset?)null), cancellationToken);

                // Word that it is in hand, to every channel the alert reached, once each, and no more about the call than the alert told it.
                var reached = await db.AlertDeliveries.Where(d => d.EventId == eventId && d.State == DeliveryState.Sent && !d.Acknowledgement)
                    .Select(d => new { d.ChannelId, d.FlowId, d.CallerOnly, Owner = db.AlertChannels.Where(c => c.Id == d.ChannelId).Select(c => c.OwnerUserId).FirstOrDefault() })
                    .ToListAsync(cancellationToken);
                var now = clock.GetUtcNow();
                foreach (var delivery in reached.Where(d => d.Owner is not { } owner || notTelling?.Contains(owner) != true).DistinctBy(d => d.ChannelId))
                {
                    db.AlertDeliveries.Add(new AlertDelivery
                    {
                        Id = Guid.NewGuid(), SiteId = alert.SiteId, EventId = eventId, FlowId = delivery.FlowId, ChannelId = delivery.ChannelId,
                        Acknowledgement = true, State = DeliveryState.Pending, NextAttemptAt = now, CreatedAt = now, CallerOnly = delivery.CallerOnly,
                    });
                }
                await db.SaveChangesAsync(cancellationToken);
            }
        }, cancellationToken);
        return found;
    }

    /// <summary>Recent calls Talk scored under <see cref="QualityStatistics.PoorBelow"/>, in or out, once each.</summary>
    private async Task RaisePoorQualityAsync(IReadOnlyList<CallLogRecord> records, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var poor = records.Where(r => r.QualityScore < QualityStatistics.PoorBelow && now - r.Time <= RecentCall).Select(r => r.Uuid).ToList();
        if (poor.Count == 0)
        {
            return;
        }

        await WithDbAsync(async (db, site) =>
        {
            var calls = await db.Calls.Include(c => c.Lines).Where(c => poor.Contains(c.TalkUuid)).ToListAsync(cancellationToken);
            var names = await CallerNames.ForAsync(db, calls.Select(c => c.FromE164), cancellationToken);
            foreach (var call in calls)
            {
                var when = TimeZoneInfo.ConvertTime(call.Time, Zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
                var who = call.Direction == "out" ? $"To {call.ToRaw}" : $"From {CallerNames.Who(call.CallerName, call.FromRaw, call.FromE164, names)} to {call.ToRaw}";
                await RaiseAsync(db, site, new AlertEvent
                {
                    Id = Guid.NewGuid(), SiteId = site, Type = AlertEventType.PoorQualityCall, Key = $"call:{call.TalkUuid}:{AlertEventType.PoorQualityCall}", At = call.Time,
                    CallId = call.Id, Title = "Poor call quality", Message = $"{who} at {when} scored {call.QualityScore} of 100 for quality.",
                }, [.. call.Lines.Select(l => new LineRef(l.Kind, l.Key))], call, cancellationToken);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Alerts about calls that have since been returned (<see cref="CallBacks"/>): calling back, or a later call getting
    /// through, is the acknowledgement. Marking a call done acknowledges its alerts there and then, as that person.
    /// </summary>
    public async Task AcknowledgeReturnedAsync(CancellationToken cancellationToken)
    {
        var returned = new List<(Guid Id, CallBackHow? How)>();
        await WithDbAsync(async (db, _) => returned = [.. (await (
                from e in db.AlertEvents
                join c in db.Calls on e.CallId equals c.Id
                where e.AcknowledgedAt == null && c.ReturnedAt != null && c.ReturnedHow != CallBackHow.MarkedDone
                select new { e.Id, c.ReturnedHow }).ToListAsync(cancellationToken)).Select(r => (r.Id, r.ReturnedHow))],
            cancellationToken);
        foreach (var (id, how) in returned)
        {
            await AcknowledgeAsync(id, how == CallBackHow.GotThrough ? ByLaterCall : ByCallBack, cancellationToken);
        }
    }

    /// <summary>One alert, for the page behind a notification's link, whoever is looking.</summary>
    public async Task<AlertEvent?> FindAsync(Guid eventId, CancellationToken cancellationToken)
    {
        AlertEvent? found = null;
        await WithDbAsync(async (db, _) => found = await db.AlertEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken), cancellationToken);
        return found;
    }

    /// <summary>Holds off escalation for a while. False if not found.</summary>
    public async Task<bool> SnoozeAsync(Guid eventId, TimeSpan duration, CancellationToken cancellationToken)
    {
        var found = false;
        await WithDbAsync(async (db, _) =>
        {
            var alert = await db.AlertEvents.SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);
            if (alert is null)
            {
                return;
            }

            found = true;
            alert.SnoozedUntil = clock.GetUtcNow() + duration;
            await db.SaveChangesAsync(cancellationToken);
        }, cancellationToken);
        return found;
    }

    /// <summary>The lines an alert is about: a call's lines, or the person a handset belongs to.</summary>
    private static async Task<HashSet<LineRef>> LinesOfAsync(TalkWatchDbContext db, AlertEvent alert, CancellationToken cancellationToken)
    {
        if (alert.CallId is { } callId)
        {
            var lines = await db.CallLines.Where(l => l.CallId == callId).Select(l => new { l.Kind, l.Key }).ToListAsync(cancellationToken);
            return [.. lines.Select(l => new LineRef(l.Kind, l.Key))];
        }

        return alert.UserUuid is { } user ? [new LineRef(LineKind.User, user)] : [];
    }

    /// <summary>
    /// A channel owned by someone who is not an admin hears only of what that person may see: a call on one of their
    /// granted lines, a handset of a person they are granted. Drift is for admins' channels and site channels.
    /// </summary>
    private static async Task<bool> OwnerMayHearAsync(TalkWatchDbContext db, AlertChannel channel, AlertEvent alert, HashSet<LineRef> lines, CancellationToken cancellationToken)
    {
        if (channel.OwnerUserId is not { } owner)
        {
            return true;
        }

        var ownerSeesAll = await db.UserRoles.AnyAsync(ur => ur.UserId == owner
            && db.RoleClaims.Any(c => c.RoleId == ur.RoleId && c.ClaimType == Permissions.ClaimType && c.ClaimValue == nameof(Permission.AllCalls)), cancellationToken);
        if (ownerSeesAll)
        {
            return true;
        }

        if (lines.Count == 0)
        {
            return false;
        }

        var grants = await db.Grants.Where(g => g.UserId == owner).Select(g => new { g.Kind, g.Key }).ToListAsync(cancellationToken);
        if (grants.Any(g => lines.Contains(new LineRef(g.Kind, g.Key))))
        {
            return true;
        }

        // A role on a number: its calls, on the DID and through what the number routes them to.
        var numbers = await db.NumberRoles.Where(n => n.UserId == owner).Select(n => n.Did).ToListAsync(cancellationToken);
        if (numbers.Count == 0)
        {
            return false;
        }

        var covered = (await db.NumberRoutes.Where(r => numbers.Contains(r.Did)).Select(r => new { r.Kind, r.Key }).ToListAsync(cancellationToken))
            .Select(r => new LineRef(r.Kind, r.Key)).Concat(numbers.Select(n => new LineRef(LineKind.Did, n)));
        return covered.Any(lines.Contains);
    }

    private async Task WithDbAsync(Func<TalkWatchDbContext, Guid, Task> work, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), scope.ServiceProvider.GetRequiredService<CurrentSite>().Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Alerting must never stop calls being stored or the live feed running.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "An alert could not be raised.")]
    private static partial void LogFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "The alert flow '{Flow}' could not be read, so it was skipped.")]
    private static partial void LogUnreadable(ILogger logger, string flow);
}

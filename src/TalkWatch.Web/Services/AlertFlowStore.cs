using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Checks and saves alert flows, for the flow editor and for <c>POST /admin/alerts/flows</c> alike. Admins may build any
/// flow. A Manager's flows are theirs: about their own lines, never about TalkWatch itself, and notifying only
/// themselves or channels they own. The database's filters show a Manager only what is theirs, so anything else they
/// name is not found.
/// </summary>
public sealed class AlertFlowStore(TalkWatchDbContext db, CurrentSite site, Audit audit, TimeProvider clock, LineDirectorySync directory)
{
    public const int MaxNameLength = 100;

    /// <summary>Saves a new flow (no id) or changes one. Returns its id, or what is wrong, in words for the person.</summary>
    public async Task<(Guid? Id, string? Problem)> SaveAsync(Guid? id, string? name, FlowDefinition? flow, ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength)
        {
            return (null, $"Give the flow a name of up to {MaxNameLength} characters.");
        }

        if (flow is null)
        {
            return (null, "That is not a flow TalkWatch can read.");
        }

        var (normalised, callers) = NormaliseCallers(flow, new NumberNormaliser(site.Region));
        if (callers is not null)
        {
            return (null, callers);
        }

        flow = normalised!;
        if (Flows.Problem(flow) is { } problem)
        {
            return (null, problem);
        }

        var admin = user.Can(Permission.ManageAlerts);
        var userId = Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var parsed) ? parsed : (Guid?)null;
        // Testing what a voicemail says reads a transcript, the most sensitive thing TalkWatch holds: for admins, who may read
        // every transcript, and not for a Manager, whose transcripts depend on each line's grant.
        if (!admin && Flows.AllConditions(flow).Any(c => c is TranscriptWordsCondition))
        {
            return (null, "Testing what a voicemail says is for admins.");
        }

        if (!admin && Flows.IsSiteAlert(flow.Trigger))
        {
            return (null, "Alerts about the whole site, such as TalkWatch itself or the Talk account, are for admins.");
        }

        if (await RecipientsProblemAsync(flow, admin, userId, cancellationToken) is { } recipients)
        {
            return (null, recipients);
        }

        // The line directory is filtered by grant, so a Manager finds only the lines they hold.
        if (!admin)
        {
            foreach (var line in Flows.AllConditions(flow).OfType<LineCondition>().SelectMany(c => c.Lines).Distinct())
            {
                if (!await db.Lines.AnyAsync(l => l.Kind == line.Kind && l.Key == line.Key, cancellationToken))
                {
                    return (null, "Choose from your own lines.");
                }
            }
        }

        var now = clock.GetUtcNow();
        AlertFlow? saved;
        if (id is { } existing)
        {
            saved = await db.AlertFlows.SingleOrDefaultAsync(f => f.Id == existing, cancellationToken);
            if (saved is null)
            {
                return (null, "That flow is not there any more.");
            }
        }
        else
        {
            saved = new AlertFlow
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = "", Definition = "{}", CreatedAt = now, OwnerUserId = admin ? null : userId,
            };
            db.AlertFlows.Add(saved);
        }

        saved.Name = name.Trim();
        saved.Trigger = flow.Trigger;
        saved.Definition = Flows.Write(flow);
        saved.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(id is null ? "alert.flow.add" : "alert.flow.change", "alert_flow", saved.Id, $"{saved.Name} {saved.Trigger}", user);
        return (saved.Id, null);
    }

    private async Task<string?> RecipientsProblemAsync(FlowDefinition flow, bool admin, Guid? userId, CancellationToken cancellationToken)
    {
        var recipients = Flows.AllNotify(flow.Steps).SelectMany(n => n.To).ToList();
        var channels = recipients.Select(r => r.Channel).OfType<Guid>().Distinct().ToList();
        if (await db.AlertChannels.CountAsync(c => channels.Contains(c.Id), cancellationToken) != channels.Count)
        {
            return admin ? "One of the channels is not there any more." : "Notify yourself or your own channels.";
        }

        // Giving someone the call-back names them as surely as notifying them does.
        var people = recipients.Select(r => r.Person).OfType<Guid>().Concat(Flows.AllAssign(flow.Steps).Select(a => a.Person)).Distinct().ToList();
        if (!admin && people.Any(p => p != userId))
        {
            return "Notify yourself or your own channels.";
        }

        if (await db.Users.CountAsync(u => people.Contains(u.Id) && u.SiteId == site.Id, cancellationToken) != people.Count)
        {
            return "One of the people is not in this site.";
        }

        // A contact is someone outside, reached at what Talk holds for them: for admins, and only with an email to send to.
        var contacts = recipients.Select(r => r.Contact).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (contacts.Count > 0)
        {
            if (!admin)
            {
                return "Notify yourself or your own channels.";
            }

            foreach (var contact in contacts)
            {
                if (directory.Current.Contacts.FirstOrDefault(c => c.Uuid == contact) is not { } known)
                {
                    return "One of the contacts is not in Talk any more.";
                }

                if (known.Address is null)
                {
                    return $"{known.DisplayName} has no email in Talk, and texting a number is not set up yet.";
                }
            }
        }

        // Whoever is free in a group reaches other people, so it is for admins, as naming another person is.
        var groups = recipients.Select(r => r.FreeIn).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (!admin && (groups.Count > 0 || recipients.Any(r => r.Rang == true)))
        {
            return "Notify yourself or your own channels.";
        }

        if (recipients.Any(r => r.Rang == true) && !Flows.IsCallAlert(flow.Trigger))
        {
            return "Whoever it rang is for call alerts: a handset or TalkWatch itself rang nobody.";
        }

        // The people behind outside contacts hear of calls they may not see, so naming them is for admins too.
        if (recipients.Any(r => r.Outside is not null))
        {
            if (!admin)
            {
                return "Notify yourself or your own channels.";
            }

            if (!Flows.IsCallAlert(flow.Trigger))
            {
                return "The people behind outside contacts are for call alerts: a handset or TalkWatch itself puts nothing through to them.";
            }

            if (recipients.Any(r => r.Outside is { } scope && !Enum.IsDefined(scope)))
            {
                return "Choose whether it reaches the people behind this call's contacts or everyone linked to one.";
            }
        }

        if (await db.Lines.CountAsync(l => l.Kind == LineKind.RingGroup && groups.Contains(l.Key), cancellationToken) != groups.Count)
        {
            return "One of the ring groups is not there any more.";
        }

        return null;
    }

    /// <summary>Every caller condition's entries as <see cref="CallerCondition.Parse"/> reads them, or the first that it cannot.</summary>
    private static (FlowDefinition? Flow, string? Problem) NormaliseCallers(FlowDefinition flow, NumberNormaliser numbers)
    {
        string? problem = null;

        FlowCondition Fix(FlowCondition condition)
        {
            if (condition is not CallerFlowCondition caller || problem is not null)
            {
                return condition;
            }

            var (entries, bad) = CallerCondition.Parse(string.Join(',', caller.Entries), numbers);
            if (bad is not null)
            {
                problem = $"'{bad}' is not a phone number, a prefix such as +44800*, or 'withheld'.";
                return condition;
            }

            return caller with { Entries = entries! };
        }

        IReadOnlyList<FlowStep> FixSteps(IReadOnlyList<FlowStep> steps) =>
            [.. steps.Select(s => s is BranchStep b ? b with { If = Fix(b.If), Then = FixSteps(b.Then), Otherwise = FixSteps(b.Otherwise) } : s)];

        var fixedFlow = flow with { Conditions = [.. flow.Conditions.Select(Fix)], Steps = FixSteps(flow.Steps) };
        return problem is null ? (fixedFlow, null) : (null, problem);
    }
}

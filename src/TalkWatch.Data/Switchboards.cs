using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;

namespace TalkWatch.Data;

/// <summary>
/// A node of the console's switchboard tree (<see cref="SwitchboardNode"/>), kept so menu options have their names and
/// greetings their length. Nodes gone from the console are kept, marked absent, since old calls passed through them.
/// </summary>
public sealed class SwitchboardNodeRow
{
    public Guid SiteId { get; set; }

    /// <summary>Talk's node id, such as swb_15.</summary>
    public required string NodeId { get; set; }

    public int? InternalId { get; set; }
    public string? Type { get; set; }
    public string? Title { get; set; }
    public int? Key { get; set; }
    public string? ParentId { get; set; }

    /// <summary>The numbers a switchboard answers, comma-separated.</summary>
    public string? Numbers { get; set; }

    public string? GreetingFile { get; set; }

    /// <summary>How long the greeting plays, measured from its audio; null until measured, or when it could not be.</summary>
    public double? GreetingSeconds { get; set; }

    public bool Present { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A menu option, with what became of the calls that chose it.</summary>
/// <param name="Label">The keys and titles on the way, such as "1 Sales › 2 New orders".</param>
public sealed record MenuOptionStatistics(string Label, int Calls, int Answered, int Missed, int Voicemail, int HungUp);

/// <summary>
/// What happened to the calls one switchboard answered in a period. Counted through the person's access scope, like the
/// dashboard, so it covers the calls they may see.
/// </summary>
/// <param name="HungUpInGreeting">Hung up before choosing anything or being put through: during the greeting.</param>
/// <param name="HungUpBeforeGreetingEnded">Of those, how many hung up before the greeting had finished playing; null when its length is not known.</param>
/// <param name="HungUpRepeatCallers">Of those, how many were from a number that has rung more than once.</param>
/// <param name="NoChoice">Calls past the greeting of a switchboard with a menu that chose nothing, and went on to its no-choice route.</param>
public sealed record SwitchboardStatistics(
    int InternalId, string Title, IReadOnlyList<string> Numbers, double? GreetingSeconds, bool HasMenu,
    int Calls, int Answered, int Missed, int Voicemail,
    int HungUpInGreeting, TimeSpan? MedianHangUp, int? HungUpBeforeGreetingEnded, int HungUpRepeatCallers,
    IReadOnlyList<MenuOptionStatistics> Options, int NoChoice)
{
    public static async Task<IReadOnlyList<SwitchboardStatistics>> ComputeAsync(TalkWatchDbContext db, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        (start, end) = (start.ToUniversalTime(), end.ToUniversalTime());
        var calls = await db.Calls.AsNoTracking()
            .Where(c => c.Direction == "in" && c.Time >= start && c.Time < end && c.Outcome != CallOutcome.Blocked)
            .Select(c => new
            {
                c.Id, c.Outcome, c.FromE164,
                Events = c.Events.OrderBy(e => e.Sequence).Select(e => new { e.Event, e.Time, e.DataJson }).ToList(),
            })
            .ToListAsync(cancellationToken);

        var journeys = calls.Select(c => new
        {
            Call = c,
            Journey = MenuJourney.Of(c.Events.Select(e => (e.Event, e.DataJson))),
            Lasted = c.Events.FirstOrDefault(e => e.Event == "call_hangup")?.Time - c.Events.FirstOrDefault(e => e.Event == "call_started")?.Time,
        }).Where(j => j.Journey.SwitchboardId is not null).ToList();

        // A repeat caller has rung more than once, ever, as far as this person can see.
        var callers = journeys.Select(j => j.Call.FromE164).OfType<string>().Distinct().ToList();
        var repeat = (await db.Calls.Where(c => c.Direction == "in" && c.FromE164 != null && callers.Contains(c.FromE164))
            .GroupBy(c => c.FromE164).Where(g => g.Count() > 1).Select(g => g.Key).ToListAsync(cancellationToken)).ToHashSet();

        var nodes = await db.SwitchboardNodes.AsNoTracking().ToListAsync(cancellationToken);
        var byInternal = nodes.Where(n => n.InternalId is not null).GroupBy(n => n.InternalId!.Value).ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.Present).First());

        var result = new List<SwitchboardStatistics>();
        foreach (var group in journeys.GroupBy(j => j.Journey.SwitchboardId!.Value))
        {
            var root = byInternal.GetValueOrDefault(group.Key);
            var hasMenu = nodes.Any(n => n.ParentId == root?.NodeId && n.Type == SwitchboardNode.Menu) || group.Any(j => j.Journey.Choices.Count > 0);
            var inGreeting = group.Where(j => j.Call.Outcome == CallOutcome.HungUpAtSwitchboard && j.Journey.Choices.Count == 0).ToList();
            var lasted = inGreeting.Select(j => j.Lasted).OfType<TimeSpan>().Order().ToList();

            string Name(MenuChoice choice) =>
                $"{choice.Key} {(byInternal.TryGetValue(choice.ItemId, out var node) && !string.IsNullOrEmpty(node.Title) ? node.Title : choice.Title)}".Trim();

            var options = group.Where(j => j.Journey.Choices.Count > 0)
                .GroupBy(j => string.Join(" › ", j.Journey.Choices.Select(Name)))
                .Select(g => new MenuOptionStatistics(g.Key, g.Count(),
                    g.Count(j => j.Call.Outcome == CallOutcome.Answered), g.Count(j => j.Call.Outcome.IsMissed()),
                    g.Count(j => j.Call.Outcome.IsVoicemail()), g.Count(j => j.Call.Outcome == CallOutcome.HungUpAtSwitchboard)))
                .OrderByDescending(o => o.Calls).ThenBy(o => o.Label, StringComparer.Ordinal)
                .ToList();

            result.Add(new SwitchboardStatistics(
                group.Key,
                root?.Title ?? $"Switchboard {group.Key}",
                root?.Numbers?.Split(',', StringSplitOptions.RemoveEmptyEntries) ?? [],
                root?.GreetingSeconds,
                hasMenu,
                group.Count(),
                group.Count(j => j.Call.Outcome == CallOutcome.Answered),
                group.Count(j => j.Call.Outcome.IsMissed()),
                group.Count(j => j.Call.Outcome.IsVoicemail()),
                inGreeting.Count,
                lasted.Count == 0 ? null : lasted.Count % 2 == 1 ? lasted[lasted.Count / 2] : (lasted[(lasted.Count / 2) - 1] + lasted[lasted.Count / 2]) / 2,
                root?.GreetingSeconds is { } greeting ? inGreeting.Count(j => j.Lasted is { } t && t.TotalSeconds < greeting) : null,
                inGreeting.Count(j => j.Call.FromE164 is { } from && repeat.Contains(from)),
                options,
                hasMenu ? group.Count(j => j.Journey.Choices.Count == 0 && j.Call.Outcome != CallOutcome.HungUpAtSwitchboard) : 0));
        }

        return [.. result.OrderByDescending(s => s.Calls)];
    }
}

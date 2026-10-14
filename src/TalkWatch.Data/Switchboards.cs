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

    /// <summary>The node as Talk last sent it; its <see cref="NodeSetup"/> is read from this. Null until first read with it kept.</summary>
    public string? Json { get; set; }

    /// <summary>SHA-256 of <see cref="Json"/> as Talk sent it, so a change is seen without comparing the whole of it.</summary>
    public string? JsonHash { get; set; }

    // Read from Json, kept beside it to filter and badge by without reading every node's JSON.
    public bool? Disabled { get; set; }
    public string? Ext { get; set; }
    public bool? MissingAudio { get; set; }
    public bool? IsDefaultTime { get; set; }

    public NodeSetup Setup => NodeSetup.Of(Json);

    /// <summary>Takes the node's setup as Talk sent it. True when it differs from what was kept, and so is worth a snapshot.</summary>
    public bool TakeSetup(SwitchboardNode node)
    {
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(node.Json)));
        var setup = node.Setup;
        (Disabled, Ext, MissingAudio, IsDefaultTime) = (setup.Disabled, setup.Ext, setup.Greeting?.Missing, setup.OpeningHours?.IsDefault);
        if (hash == JsonHash)
        {
            return false;
        }

        (Json, JsonHash) = (node.Json, hash);
        return true;
    }
}

/// <summary>
/// A switchboard node as it was from <see cref="CapturedAt"/>, kept whenever its setup changed, so a past call can be
/// explained by the setup it met and a change can be compared before and after. Kept as long as calls are.
/// </summary>
public sealed class SwitchboardNodeSnapshot
{
    public long Id { get; set; }
    public Guid SiteId { get; set; }
    public required string NodeId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
    public required string JsonHash { get; set; }
    public required string Json { get; set; }
}

/// <summary>A menu option, with what became of the calls that chose it.</summary>
/// <param name="Label">The keys and titles on the way, such as "1 Sales › 2 New orders".</param>
public sealed record MenuOptionStatistics(string Label, int Calls, int Answered, int Missed, int Voicemail, int HungUp)
{
    /// <summary>The option's node, the last one chosen on the way; null when the switchboard no longer has it.</summary>
    public string? NodeId { get; init; }

    /// <summary>How its calls divided between the branches of an opening-hours split under it; empty when there is none.</summary>
    public HoursSplit? Hours { get; init; }
}

/// <summary>
/// The branches of an opening-hours split and the calls each took. Talk logs no step for passing a split, so each call's
/// branch is worked out from its time and the branches' hours as they were then: the setup kept at or before the call,
/// or the first kept when the call came before any.
/// </summary>
/// <param name="Unplaced">Calls whose branch can't be said: holidays on with dates TalkWatch can't read, or no setup kept.</param>
public sealed record HoursSplit(IReadOnlyList<BranchStatistics> Branches, int Unplaced);

/// <summary>One branch of an opening-hours split, with what became of the calls it took.</summary>
public sealed record BranchStatistics(string NodeId, string Label, bool OutOfHours, int Calls, int Answered, int Missed, int Voicemail);

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
    /// <summary>How calls that chose nothing divided between the branches of an opening-hours split on the switchboard itself.</summary>
    public HoursSplit? Hours { get; init; }

    /// <param name="zone">The zone opening hours are read in; without one, no split is worked out.</param>
    public static async Task<IReadOnlyList<SwitchboardStatistics>> ComputeAsync(
        TalkWatchDbContext db, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken, TimeZoneInfo? zone = null)
    {
        (start, end) = (start.ToUniversalTime(), end.ToUniversalTime());
        var calls = await db.Calls.AsNoTracking()
            .Where(c => c.Direction == "in" && c.Time >= start && c.Time < end && c.Outcome != CallOutcome.Blocked)
            .Select(c => new
            {
                c.Id, c.Outcome, c.FromE164, c.Time,
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
        var splits = zone is null ? null : await HoursSplits.ReadAsync(db, nodes, zone, cancellationToken);

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
                .Select(g =>
                {
                    var option = byInternal.GetValueOrDefault(g.First().Journey.Choices[^1].ItemId);
                    return new MenuOptionStatistics(g.Key, g.Count(),
                        g.Count(j => j.Call.Outcome == CallOutcome.Answered), g.Count(j => j.Call.Outcome.IsMissed()),
                        g.Count(j => j.Call.Outcome.IsVoicemail()), g.Count(j => j.Call.Outcome == CallOutcome.HungUpAtSwitchboard))
                    {
                        NodeId = option?.NodeId,
                        Hours = option is null ? null : splits?.Of(option.NodeId, [.. g.Where(j => j.Call.Outcome != CallOutcome.HungUpAtSwitchboard).Select(j => (j.Call.Time, j.Call.Outcome))]),
                    };
                })
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
                hasMenu ? group.Count(j => j.Journey.Choices.Count == 0 && j.Call.Outcome != CallOutcome.HungUpAtSwitchboard) : 0)
            {
                Hours = root is null ? null : splits?.Of(root.NodeId,
                    [.. group.Where(j => j.Journey.Choices.Count == 0 && j.Call.Outcome != CallOutcome.HungUpAtSwitchboard).Select(j => (j.Call.Time, j.Call.Outcome))]),
            });
        }

        return [.. result.OrderByDescending(s => s.Calls)];
    }
}

/// <summary>Each opening-hours split's branches as they were set up over time, from the snapshots kept of them.</summary>
internal sealed class HoursSplits
{
    private readonly ILookup<string, SwitchboardNodeRow> _children;
    private readonly Dictionary<string, List<SwitchboardNodeSnapshot>> _snapshots;
    private readonly Dictionary<string, SwitchboardNodeRow> _rows;
    private readonly TimeZoneInfo _zone;
    private readonly Dictionary<string, SwitchboardNode?> _parsed = new(StringComparer.Ordinal);

    private HoursSplits(IReadOnlyList<SwitchboardNodeRow> nodes, List<SwitchboardNodeSnapshot> snapshots, TimeZoneInfo zone)
    {
        _children = nodes.Where(n => n.Present && n.ParentId is not null).ToLookup(n => n.ParentId!, StringComparer.Ordinal);
        _rows = nodes.ToDictionary(n => n.NodeId, StringComparer.Ordinal);
        _snapshots = snapshots.GroupBy(s => s.NodeId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.OrderBy(s => s.CapturedAt).ToList(), StringComparer.Ordinal);
        _zone = zone;
    }

    public static async Task<HoursSplits> ReadAsync(TalkWatchDbContext db, IReadOnlyList<SwitchboardNodeRow> nodes, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var timed = nodes.Where(n => n.Type == "time").Select(n => n.NodeId).ToList();
        var snapshots = await db.SwitchboardNodeSnapshots.AsNoTracking().Where(s => timed.Contains(s.NodeId)).ToListAsync(cancellationToken);
        return new HoursSplits(nodes, snapshots, zone);
    }

    /// <summary>How these calls divided between the branches of the split under a node; null when it has none.</summary>
    public HoursSplit? Of(string nodeId, IReadOnlyList<(DateTimeOffset Time, CallOutcome Outcome)> calls)
    {
        var branches = _children[nodeId].Where(n => n.Type == "time").ToList();
        if (branches.Count == 0)
        {
            return null;
        }

        var took = new Dictionary<string, List<CallOutcome>>(StringComparer.Ordinal);
        var unplaced = 0;
        foreach (var (time, outcome) in calls)
        {
            var then = branches.Select(b => SetupAt(b.NodeId, time)).OfType<SwitchboardNode>().ToList();
            if (then.Count == branches.Count && OpeningHoursEvaluator.Branch(then, time, _zone) is { } branch)
            {
                (took.TryGetValue(branch.Id, out var list) ? list : took[branch.Id] = []).Add(outcome);
            }
            else
            {
                unplaced++;
            }
        }

        return new HoursSplit(
            [.. branches.OrderBy(b => b.IsDefaultTime == true).ThenBy(b => b.Title, StringComparer.Ordinal).Select(b =>
            {
                var outcomes = took.GetValueOrDefault(b.NodeId) ?? [];
                return new BranchStatistics(b.NodeId, string.IsNullOrWhiteSpace(b.Title) ? (b.IsDefaultTime == true ? "Out of hours" : "Opening hours") : b.Title!,
                    b.IsDefaultTime == true, outcomes.Count, outcomes.Count(o => o == CallOutcome.Answered), outcomes.Count(o => o.IsMissed()), outcomes.Count(o => o.IsVoicemail()));
            })],
            unplaced);
    }

    // The branch as it was set up at a time: the setup kept at or before it, or the first kept when the time came before
    // any, on the footing that it had not changed before TalkWatch first saw it; the node as it is now when none is kept.
    private SwitchboardNode? SetupAt(string nodeId, DateTimeOffset time)
    {
        var json = _snapshots.TryGetValue(nodeId, out var kept) && kept.Count > 0
            ? (kept.LastOrDefault(s => s.CapturedAt <= time) ?? kept[0]).Json
            : _rows.GetValueOrDefault(nodeId)?.Json;
        if (json is null)
        {
            return null;
        }

        var key = nodeId + "\n" + json;
        if (!_parsed.TryGetValue(key, out var node))
        {
            _parsed[key] = node = Switchboard.ParseNode(nodeId, json);
        }

        return node;
    }
}

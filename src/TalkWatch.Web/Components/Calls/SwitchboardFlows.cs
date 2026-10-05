using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Components.Calls;

/// <summary>
/// One place a call can be in a switchboard, drawn as a stage on Operator's flow: calls coming in, the menu, each
/// option, and where an option puts calls through to. Stages sit in columns by how far into the switchboard they are,
/// and each is joined to the one it is reached from.
/// </summary>
/// <param name="Id">The stage's key on the page: "in", "hungup", or "n:" and Talk's node id.</param>
/// <param name="From">The stage it is reached from, or null for calls coming in.</param>
/// <param name="Kind">in, menu, option, destination or hungup: how it is drawn.</param>
/// <param name="Today">Calls through it today, which sets how loud its stream is.</param>
public sealed record FlowStage(string Id, string? From, int Column, string Label, string Kind, int Today)
{
    /// <summary>
    /// Whether <see cref="Today"/> is this stage's own count. Past an opening-hours split Talk logs nothing, so a branch
    /// and what it leads to only carry the option's traffic on, to set their stream; their count is not shown.
    /// </summary>
    public bool Counted { get; init; } = true;
}

/// <summary>A caller in the switchboard now: the stage they are at, and the step that put them there.</summary>
/// <param name="Mark">The status mark: routing while in the menu, ringing once an option puts them through.</param>
public sealed record FlowCaller(string Key, string StageId, string Who, string Number, string Step, string Mark, DateTimeOffset Since);

/// <summary>One switchboard as Operator draws it, live.</summary>
public sealed record SwitchboardFlow(int Id, string Title, IReadOnlyList<string> Numbers, IReadOnlyList<FlowStage> Stages, IReadOnlyList<FlowCaller> Callers, int HungUpToday)
{
    public int Columns => Stages.Count == 0 ? 0 : Stages.Max(s => s.Column) + 1;

    public IEnumerable<FlowCaller> At(string stageId) => Callers.Where(c => c.StageId == stageId).OrderBy(c => c.Since);

    public int InTheMenu => Callers.Count(c => c.Mark == "routing");

    /// <summary>
    /// Where each stage sits down the flow, in rows: the stages nothing leads on from take a row each, in order, and a
    /// stage sits level with the middle of what it leads to, so each branch fans out from its stage as a tree.
    /// </summary>
    public IReadOnlyDictionary<string, double> Rows => _rows ??= Lay();

    /// <summary>How many rows the flow needs.</summary>
    public int RowCount => Rows.Count == 0 ? 0 : (int)Rows.Values.Max() + 1;

    private Dictionary<string, double>? _rows;

    private Dictionary<string, double> Lay()
    {
        var rows = new Dictionary<string, double>(StringComparer.Ordinal);
        var next = 0;
        double Place(FlowStage stage)
        {
            var onward = Stages.Where(s => s.From == stage.Id).ToList();
            var row = onward.Count == 0 ? next++ : onward.Select(Place).ToList() is var placed ? (placed[0] + placed[^1]) / 2 : 0;
            rows[stage.Id] = row;
            return row;
        }

        foreach (var start in Stages.Where(s => s.From is null))
        {
            Place(start);
        }

        return rows;
    }
}

/// <summary>A call from today, as far as the flow needs it: where it went in the switchboard and how it ended.</summary>
public sealed record FlowHistory(MenuJourney Journey, CallOutcome Outcome);

public static class SwitchboardFlows
{
    /// <summary>
    /// The switchboards to draw, each with its stages, today's traffic and the callers in it now. With a number chosen
    /// (<paramref name="chosen"/>, E.164), only the switchboards that number reaches; without one, every switchboard on
    /// the console.
    /// </summary>
    public static IReadOnlyList<SwitchboardFlow> Build(
        IReadOnlyList<SwitchboardNodeRow> nodes, IReadOnlyList<FlowHistory> today, IReadOnlyList<(LiveCall Call, MenuJourney Journey)> live, string? chosen, Func<string?, string?> toE164)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(today);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(toE164);
        var present = nodes.Where(n => n.Present).ToList();
        var children = present.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!, StringComparer.Ordinal);
        var flows = new List<SwitchboardFlow>();
        foreach (var root in present.Where(n => n.Type == SwitchboardNode.Root && n.InternalId is not null).OrderBy(n => n.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var numbers = (root.Numbers ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (chosen is not null && !numbers.Any(n => toE164(n) == chosen))
            {
                continue;
            }

            var id = root.InternalId!.Value;
            var through = today.Where(c => c.Journey.SwitchboardId == id).ToList();
            var rootStage = "n:" + root.NodeId;
            var stages = new List<FlowStage>
            {
                new("in", null, 0, "Calling in", "in", through.Count),
                new(rootStage, "in", 1, string.IsNullOrWhiteSpace(root.Title) ? "Switchboard" : root.Title!, "menu", through.Count),
            };

            // Each option and where it leads, a column further in at each step. Opening-hours splits pass through.
            var byInternal = new Dictionary<int, string>();
            void Add(SwitchboardNodeRow parent, string parentStage, int column)
            {
                foreach (var node in children[parent.NodeId].OrderBy(n => n.Key ?? int.MaxValue).ThenBy(n => n.Title, StringComparer.Ordinal))
                {
                    var stage = "n:" + node.NodeId;
                    var isMenu = node.Type == SwitchboardNode.Menu;
                    var nested = isMenu && children[node.NodeId].Any(c => c.Type == SwitchboardNode.Menu);
                    // A menu option counts the calls that chose it; an opening-hours split, and where calls end up, carry
                    // on the traffic of the option before them, since Talk logs no step for passing through them.
                    var count = isMenu && node.InternalId is { } internalId
                        ? through.Count(c => c.Journey.Choices.Any(ch => ch.ItemId == internalId))
                        : stages.First(s => s.Id == parentStage).Today;
                    var label = isMenu && node.Key is { } key ? $"{key} · {node.Title}" : !string.IsNullOrWhiteSpace(node.Title) ? node.Title! : Kind(node.Type);
                    var kind = nested ? "menu" : isMenu ? "option" : node.Type == "time" ? "hours" : "destination";
                    var before = stages.First(s => s.Id == parentStage);
                    stages.Add(new FlowStage(stage, parentStage, column, label, kind, count) { Counted = isMenu || (before.Counted && before.Kind != "hours" && kind != "hours") });
                    if (node.InternalId is { } nodeId)
                    {
                        byInternal[nodeId] = stage;
                    }

                    Add(node, stage, column + 1);
                }
            }

            Add(root, rootStage, 2);
            var hungUp = through.Count(c => c.Outcome == CallOutcome.HungUpAtSwitchboard);
            stages.Add(new FlowStage("hungup", rootStage, 2, "Hung up in the menu", "hungup", hungUp));

            var callers = new List<FlowCaller>();
            foreach (var (call, journey) in live.Where(l => l.Call.Live && l.Call.State is "menu" or "ringing" or "routing" or "voicemail"))
            {
                if (journey.SwitchboardId != id)
                {
                    continue;
                }

                var at = journey.Choices.Count > 0 && byInternal.TryGetValue(journey.Choices[^1].ItemId, out var chosenStage) ? chosenStage : rootStage;
                // Put through: the caller is with where the option leads, when it leads to one place.
                if (call.State == "ringing")
                {
                    var onward = stages.Where(s => s.From == at && s.Kind == "destination").ToList();
                    if (onward.Count == 1)
                    {
                        at = onward[0].Id;
                    }
                }

                var step = call.Chronicle.Steps.LastOrDefault(s => s.Kind is StepKind.Menu or StepKind.Ringing or StepKind.Skipped or StepKind.Voicemail);
                callers.Add(new FlowCaller(call.Call.TalkUuid, at, call.Who, call.Number, step?.Text ?? LiveCalls.Words(call.State), LiveCalls.Mark(call.State), step?.Time ?? call.Since));
            }

            flows.Add(new SwitchboardFlow(id, stages[1].Label, numbers, stages, callers, hungUp));
        }

        return flows;
    }

    // A node Talk gives no name: said by what it is.
    private static string Kind(string? type) => type switch
    {
        "group" => "Ring group",
        "user" => "Person",
        "contact" => "Outside number",
        "time" => "Opening hours",
        "voicemail" => "Voicemail",
        _ => "Option",
    };
}

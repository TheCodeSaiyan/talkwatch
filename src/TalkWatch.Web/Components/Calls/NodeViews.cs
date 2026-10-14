using System.Globalization;
using System.Text.Json;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Components.Calls;

/// <summary>The one badge a switchboard node carries on Operator's flow. A node's other flags are in its panel.</summary>
public enum NodeBadge
{
    None,

    /// <summary>An opening-hours branch whose hours cover now.</summary>
    OpenNow,

    /// <summary>An opening-hours branch calls are not taking now.</summary>
    NotNow,

    /// <summary>Holidays are on but their dates cannot be read, so the branch taking calls is not known.</summary>
    HoursUnknown,

    /// <summary>Switched off in Talk, itself or above it.</summary>
    Off,

    MissingAudio,
    AudioPending,

    /// <summary>Nothing to put callers through to.</summary>
    GoesNowhere,
}

/// <summary>Somewhere a node puts calls: a menu option by its key, an opening-hours branch, or where calls end up.</summary>
/// <param name="Key">The key a caller presses, for a menu option.</param>
/// <param name="Detail">Where it leads in turn, such as "Rings Sales team".</param>
public sealed record NodeRoute(string NodeId, int? Key, string Label, string? Detail, NodeBadge Badge);

/// <summary>A change to a node's setup, from two snapshots in a row.</summary>
public sealed record NodeChange(DateTimeOffset At, string Words);

/// <summary>What only admins see: who a caller may dial, who gets voicemail, and how the setup has changed.</summary>
public sealed record NodeSetupView(
    string? DialByExt, IReadOnlyList<string> AllowList, IReadOnlyList<string> Receivers, string? HoldMusic, string? Greeting, IReadOnlyList<NodeChange> Changes,
    IReadOnlyList<HealthFlag> DraftFlags);

/// <summary>
/// A switchboard node as its panel on Operator shows it. Anyone who may see the switchboard gets the summary; only an
/// admin's has <see cref="Setup"/>, so the rest is never sent to anyone else's browser.
/// </summary>
/// <param name="Context">Where it is, such as "Main line · menu option, key 1".</param>
/// <param name="Until">For a node split by hours, when the branch taking calls now stops, such as "until 17:00".</param>
/// <param name="Week">The hours to draw for the week: its own, or those of the split under it.</param>
/// <param name="Flags">Health flags a caller meets now; switched-off nodes raise none here.</param>
public sealed record NodePanel(
    string NodeId, string Title, string Context, string? Ext, NodeBadge Badge, string? Until, Greeting? Greeting, bool CanPlay,
    IReadOnlyList<NodeRoute> Routes, string? NoKeyPress, OpeningHours? Week, IReadOnlyList<HealthFlag> Flags, string Zone, NodeSetupView? Setup);

public static class NodeViews
{
    /// <summary>The kept nodes read back as Talk sent them; nodes kept before their JSON was are left out.</summary>
    public static IReadOnlyList<SwitchboardNode> Nodes(IEnumerable<SwitchboardNodeRow> rows)
    {
        var present = rows.Where(r => r.Present && r.Json is not null).ToList();
        if (present.Count == 0)
        {
            return [];
        }

        var byId = string.Join(',', present.Select(r => JsonSerializer.Serialize(r.NodeId) + ":" + r.Json));
        return Switchboard.Parse("{\"entities\":{\"nodes\":{\"byId\":{" + byId + "}}}}");
    }

    /// <summary>Each node's badge, as operators see it: switched off first, then what is wrong, then its hours.</summary>
    public static IReadOnlyDictionary<string, NodeBadge> Badges(IReadOnlyList<SwitchboardNode> nodes, DateTimeOffset now, TimeZoneInfo zone)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var flags = NodeHealth.Of(nodes).Where(f => !f.Disabled).ToLookup(f => f.NodeId, StringComparer.Ordinal);
        var hours = HoursBadges(nodes, now, zone);
        var badges = new Dictionary<string, NodeBadge>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var mine = flags[node.Id].Select(f => f.Issue).ToHashSet();
            badges[node.Id] =
                NodeHealth.IsDisabled(node, byId) ? NodeBadge.Off
                : mine.Overlaps([HealthIssue.EmptyTarget, HealthIssue.ClosedBranchWithNoAction, HealthIssue.NoKeyPressTargetMissing]) ? NodeBadge.GoesNowhere
                : mine.Contains(HealthIssue.MissingAudio) ? NodeBadge.MissingAudio
                : mine.Contains(HealthIssue.GeneratingAudio) ? NodeBadge.AudioPending
                : hours.GetValueOrDefault(node.Id);
        }

        return badges;
    }

    /// <summary>The panel for one node, or null when the switchboard has no such node.</summary>
    /// <param name="name">A user's or group's name from its uuid or numeric id, or null when not known.</param>
    /// <param name="snapshots">The node's kept setups, oldest first; only read for an admin.</param>
    public static NodePanel? Panel(
        string nodeId, IReadOnlyList<SwitchboardNode> nodes, DateTimeOffset now, TimeZoneInfo zone, bool admin, Func<string, string?> name, IReadOnlyList<SwitchboardNodeSnapshot> snapshots)
    {
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        if (!byId.TryGetValue(nodeId, out var node))
        {
            return null;
        }

        var children = nodes.Where(n => n.ParentId is not null).ToLookup(n => n.ParentId!, StringComparer.Ordinal);
        var badges = Badges(nodes, now, zone);
        var setup = node.Setup;
        var under = Ordered(children[node.Id]);

        // Where it goes: its options or branches, each with where that leads in turn.
        string? Onward(SwitchboardNode n) => Ordered(children[n.Id]).Select(Leads).FirstOrDefault();
        var routes = under.Select(n => new NodeRoute(n.Id, n.Type == SwitchboardNode.Menu ? n.Key : null, Label(n), Onward(n), badges.GetValueOrDefault(n.Id))).ToList();

        // The hours to show: the node's own, or the split under it.
        var week = setup.OpeningHours is { IsDefault: false } own ? own
            : under.Select(n => n.Setup.OpeningHours).FirstOrDefault(h => h is { IsDefault: false });
        var timed = setup.OpeningHours is not null ? [.. children[node.ParentId ?? ""].Where(n => n.Type == "time")] : under.Where(n => n.Type == "time").ToList();
        var until = timed.Count > 0 ? Until(timed, now, zone) : null;

        var root = Up(node, byId).LastOrDefault(n => n.Type == SwitchboardNode.Root) ?? node;
        var kind = node.Type switch
        {
            SwitchboardNode.Root => "switchboard",
            SwitchboardNode.Menu => node.Key is { } key ? $"menu option, key {key}" : "menu option",
            "time" => node.Setup.OpeningHours?.IsDefault == true ? "out-of-hours branch" : "opening-hours branch",
            "group" => "ring group",
            "user" => "person",
            "contact" => "outside number",
            _ => "node",
        };
        var context = root == node ? (node.Numbers.Count > 0 ? $"switchboard · {string.Join(", ", node.Numbers)}" : "switchboard") : $"{Label(root)} · {kind}";
        var health = NodeHealth.Of(nodes).Where(f => f.NodeId == node.Id).ToList();

        NodeSetupView? adminView = null;
        if (admin)
        {
            var dial = setup.DialByExt;
            var allowNames = dial is { LimitedToAllowList: true } ? dial.AllowList.Select(id => name(id) ?? id).ToList() : [];
            var receivers = (setup.Receivers ?? []).Concat(setup.NoKeyPress?.VoicemailReceivers ?? []).Distinct(StringComparer.Ordinal).Select(id => name(id) ?? id).ToList();
            adminView = new NodeSetupView(
                dial is null ? null : !dial.Enabled ? "Off" : dial.LimitedToAllowList ? "On, limited to an allow-list" : "On, any extension",
                allowNames, receivers, HoldMusic(node.Json), GreetingWords(setup.Greeting), Changes(snapshots),
                [.. health.Where(f => f.Disabled)]);
        }

        return new NodePanel(
            node.Id, Label(node), context, setup.Ext, badges.GetValueOrDefault(node.Id), until, setup.Greeting,
            node.GreetingFileName is not null && node.Type is SwitchboardNode.Root or SwitchboardNode.Menu or "time",
            routes, NoKeyPressWords(setup.NoKeyPress, byId), week, [.. health.Where(f => !f.Disabled)], zone.Id, adminView);
    }

    public static string Key(NodeBadge badge) => badge switch
    {
        NodeBadge.OpenNow => "open",
        NodeBadge.NotNow => "closed",
        NodeBadge.HoursUnknown => "unknown",
        NodeBadge.Off => "off",
        NodeBadge.MissingAudio => "noaudio",
        NodeBadge.AudioPending => "pending",
        NodeBadge.GoesNowhere => "broken",
        _ => "",
    };

    public static string Words(NodeBadge badge) => badge switch
    {
        NodeBadge.OpenNow => "Open now",
        NodeBadge.NotNow => "Not now",
        NodeBadge.HoursUnknown => "Hours unknown",
        NodeBadge.Off => "Off",
        NodeBadge.MissingAudio => "No audio",
        NodeBadge.AudioPending => "Audio pending",
        NodeBadge.GoesNowhere => "Goes nowhere",
        _ => "",
    };

    public static string Why(NodeBadge badge) => badge switch
    {
        NodeBadge.OpenNow => "Open by its hours",
        NodeBadge.NotNow => "Calls aren't taking this branch now",
        NodeBadge.HoursUnknown => "Holidays are on but TalkWatch can't read their dates, so it doesn't guess which branch is taking calls",
        NodeBadge.Off => "Switched off in Talk: callers can't reach it",
        NodeBadge.MissingAudio => "Talk says its audio is missing, so callers hear silence",
        NodeBadge.AudioPending => "Talk is still generating its audio",
        NodeBadge.GoesNowhere => "There's nothing to put callers through to",
        _ => "",
    };

    /// <summary>A health flag as the panel says it.</summary>
    public static (string Title, string Detail) Words(HealthFlag flag) => flag.Issue switch
    {
        HealthIssue.MissingAudio => ("Its greeting has no audio", "Callers hear silence here. Re-record or regenerate it in Talk."),
        HealthIssue.GeneratingAudio => ("Its audio is still being generated", "Until Talk finishes, callers may hear the old greeting or nothing."),
        HealthIssue.EmptyTarget => ("It goes nowhere", "There's nothing under it to put callers through to."),
        HealthIssue.EmptyAllowList => ("Dial by extension reaches nobody", "It's limited to an allow-list with nobody on it."),
        HealthIssue.ClosedBranchWithNoAction => ("Out of hours goes nowhere", "Its split by opening hours has no out-of-hours branch, or one with nothing under it."),
        HealthIssue.NoKeyPressTargetMissing => ("Pressing nothing leads to a node that's gone", "Talk sends callers who press nothing to a node no longer in the switchboard."),
        _ => (flag.Issue.ToString(), ""),
    };

    /// <summary>A day's ranges in clock times, such as "09:00–17:00"; "Closed" for none.</summary>
    public static string Ranges(IReadOnlyList<MinuteRange> ranges) =>
        ranges.Count == 0 ? "Closed" : string.Join(", ", ranges.Select(r => $"{Clock(r.From)}–{Clock(r.To)}"));

    private static string Clock(int minutes) => $"{minutes / 60 % 24:00}:{minutes % 60:00}";

    private static Dictionary<string, NodeBadge> HoursBadges(IReadOnlyList<SwitchboardNode> nodes, DateTimeOffset now, TimeZoneInfo zone)
    {
        var badges = new Dictionary<string, NodeBadge>(StringComparer.Ordinal);
        foreach (var split in nodes.Where(n => n.Type == "time").GroupBy(n => n.ParentId ?? "", StringComparer.Ordinal))
        {
            var branches = split.ToList();
            var unknown = branches.Any(n => n.Setup.OpeningHours is { IsDefault: false } h && OpeningHoursEvaluator.Evaluate(h, now, zone) == OpenState.Unknown);
            var taking = unknown ? null : OpeningHoursEvaluator.Branch(branches, now, zone);
            foreach (var branch in branches)
            {
                badges[branch.Id] = unknown ? NodeBadge.HoursUnknown : branch == taking ? NodeBadge.OpenNow : NodeBadge.NotNow;
            }
        }

        return badges;
    }

    // When the branch taking calls now stops: the end of the range it is in, for a branch with hours.
    private static string? Until(IReadOnlyList<SwitchboardNode> timed, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (OpeningHoursEvaluator.Branch(timed, now, zone) is not { } taking || taking.Setup.OpeningHours is not { IsDefault: false } hours)
        {
            return null;
        }

        var local = TimeZoneInfo.ConvertTime(now, zone);
        var minute = (int)local.TimeOfDay.TotalMinutes;
        var today = hours.Days.TryGetValue(local.DayOfWeek, out var r) ? r : [];
        var range = today.FirstOrDefault(x => minute >= x.From && minute < (x.To <= x.From ? x.To + 1440 : x.To));
        return range == default ? null : $"until {Clock(range.To)}";
    }

    private static List<SwitchboardNode> Ordered(IEnumerable<SwitchboardNode> nodes) =>
        [.. nodes.OrderBy(n => n.Key ?? int.MaxValue).ThenBy(n => n.Setup.OpeningHours?.IsDefault == true).ThenBy(n => n.Title, StringComparer.Ordinal)];

    private static IEnumerable<SwitchboardNode> Up(SwitchboardNode node, Dictionary<string, SwitchboardNode> byId)
    {
        for (SwitchboardNode? at = node; at is not null; at = at.ParentId is { } p && byId.TryGetValue(p, out var up) ? up : null)
        {
            yield return at;
        }
    }

    private static string Label(SwitchboardNode node) =>
        !string.IsNullOrWhiteSpace(node.Title) ? node.Title!
        : node.Setup.OpeningHours is { } h ? (h.IsDefault ? "Out of hours" : "Opening hours")
        : node.Type switch { "group" => "Ring group", "user" => "Person", "contact" => "Outside number", _ => "Option" };

    private static string Leads(SwitchboardNode node)
    {
        var action = node.Setup.CallAction == "voicemail" ? "Voicemail at " : null;
        return node.Type switch
        {
            "group" => (action ?? "Rings ") + Label(node),
            "user" or "contact" => (action ?? "Rings ") + Label(node),
            SwitchboardNode.Menu => "Menu " + Label(node),
            "time" => "By its hours",
            _ => Label(node),
        };
    }

    private static string? NoKeyPressWords(NoKeyPress? noKeyPress, Dictionary<string, SwitchboardNode> byId) => noKeyPress switch
    {
        null => null,
        { TransferToNodeId: { } target } => byId.TryGetValue(target, out var to) || (to = byId.Values.FirstOrDefault(n => n.InternalId?.ToString(CultureInfo.InvariantCulture) == target)) is not null
            ? "Goes to " + Label(to!) : "Goes to a node that's gone",
        { Action: "drop_call" } => "Drops the call",
        { Action: "voicemail" } => "Goes to voicemail",
        { Action: { } other } => other.Replace('_', ' '),
        _ => null,
    };

    private static string? GreetingWords(Greeting? greeting) =>
        greeting is null ? null
        : string.Join(" · ", new[]
        {
            greeting.Type switch { "generated" => "Generated from text", "uploaded" => "Uploaded", "default" => "Talk's default", var t => t },
            greeting.FileName,
        }.Where(s => !string.IsNullOrEmpty(s)));

    private static string? HoldMusic(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("hold_music", out var music) && music.ValueKind == JsonValueKind.Object
            ? string.Join(" · ", new[] { Text(music, "type") is { } t ? char.ToUpperInvariant(t[0]) + t[1..] : null, Text(music, "title") }.OfType<string>())
            : null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s ? s : null;

    // Fields that change without anyone changing the setup: the waveform is redrawn from the same greeting.
    private static readonly HashSet<string> Noise = ["greeting_waveform_data", "generating_audio", "childNodeIds", "child_node_keypress_order"];

    /// <summary>What changed between each kept setup and the one before it, newest first, ending with when it was first seen.</summary>
    public static IReadOnlyList<NodeChange> Changes(IReadOnlyList<SwitchboardNodeSnapshot> snapshots)
    {
        var changes = new List<NodeChange>();
        for (var i = 0; i < snapshots.Count; i++)
        {
            if (i == 0)
            {
                changes.Add(new NodeChange(snapshots[0].CapturedAt, "First seen"));
                continue;
            }

            using var before = JsonDocument.Parse(snapshots[i - 1].Json);
            using var after = JsonDocument.Parse(snapshots[i].Json);
            var fields = before.RootElement.EnumerateObject().Select(p => p.Name).Union(after.RootElement.EnumerateObject().Select(p => p.Name))
                .Where(f => !Noise.Contains(f))
                .Where(f => !(before.RootElement.TryGetProperty(f, out var a) && after.RootElement.TryGetProperty(f, out var b) && JsonElement.DeepEquals(a, b)))
                .ToList();
            var words = fields.Select(f => f switch
            {
                "disabled" => after.RootElement.TryGetProperty(f, out var d) && d.ValueKind == JsonValueKind.True ? "Switched off" : "Switched on",
                "greeting" or "greeting_file_name" or "greeting_type" or "voice_type" or "voice_language" => "Greeting changed",
                "missing_audio" => after.RootElement.TryGetProperty(f, out var m) && m.ValueKind == JsonValueKind.True ? "Audio went missing" : "Audio back",
                "day_ranges" or "holidays" or "holidays_enabled" or "is_default_time_node" => "Hours changed",
                "ext_dialing" => "Dial by extension changed",
                "title" or "group_name" or "first_name" or "last_name" => "Renamed",
                "key" => "Key changed",
                "path" => "Moved",
                _ when f.StartsWith("noaction", StringComparison.Ordinal) || f == "sa_menu_noaction" => "No-key-press action changed",
                _ => "Setup changed",
            }).Distinct().ToList();
            if (words.Count > 0)
            {
                changes.Add(new NodeChange(snapshots[i].CapturedAt, string.Join(", ", words)));
            }
        }

        changes.Reverse();
        return changes;
    }
}

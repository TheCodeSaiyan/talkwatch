using System.Text.Json;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Talk;

/// <summary>
/// One node of the console's switchboard tree (GET /proxy/talk/api/switchboard), as Talk 5.3.2 sends it. A root is a
/// switchboard callers reach on its numbers; an ivr node is a menu option, reached by its key, that may hold a menu of
/// its own; time nodes split by opening hours; group and contact nodes are where calls end up.
/// </summary>
/// <param name="Id">Talk's node id, such as swb_15 or grp_39_1.</param>
/// <param name="InternalId">The id calls and menu events use: to_smart_attendant_id for a root, sa_id and sa_item_id in entered_sa_menu.</param>
/// <param name="ParentId">The node above, or null for a root.</param>
/// <param name="GreetingFileName">The greeting or prompt it plays, at /proxy/talk/api/switchboard/audio/{file}.</param>
public sealed record SwitchboardNode(
    string Id, int? InternalId, string? Type, string? Title, int? Key, string? ParentId, IReadOnlyList<string> Numbers, string? GreetingFileName)
{
    public const string Root = "root", Menu = "ivr";

    /// <summary>For a group node, the ring group it puts calls through to (Talk's numeric id).</summary>
    public string? GroupId { get; init; }

    /// <summary>For a user node, the Talk user it puts calls through to (uuid).</summary>
    public string? UserUuid { get; init; }

    /// <summary>For a contact node, the contact it puts calls through to (uuid).</summary>
    public string? ContactUuid { get; init; }
}

public static class Switchboard
{
    /// <summary>The nodes of a switchboard response, the container left out. Throws <see cref="JsonException"/> on another shape.</summary>
    public static IReadOnlyList<SwitchboardNode> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var byId = document.RootElement.GetProperty("entities").GetProperty("nodes").GetProperty("byId");
        var nodes = new List<SwitchboardNode>();
        foreach (var property in byId.EnumerateObject())
        {
            var node = property.Value;
            if (property.Name == "container" || node.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var path = node.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.Array ? p.EnumerateArray().Select(e => e.GetString()).ToList() : [];
            var parent = path.LastOrDefault();
            nodes.Add(new SwitchboardNode(
                property.Name,
                Int(node, "internal_id"),
                Text(node, "type"),
                Text(node, "title"),
                Int(node, "key"),
                parent is null or "container" ? null : parent,
                node.TryGetProperty("numbers", out var n) && n.ValueKind == JsonValueKind.Array ? [.. n.EnumerateArray().Select(e => e.GetString()).OfType<string>()] : [],
                Text(node, "greeting_file_name"))
            {
                GroupId = Int(node, "group_id")?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? Text(node, "group_id"),
                UserUuid = Text(node, "user_uuid") ?? Text(node, "user_id"),
                ContactUuid = Text(node, "contact_uuid"),
            });
        }

        return nodes;
    }

    private static string? Text(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Int(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;
}

/// <summary>A menu option a caller chose: in menu <see cref="MenuId"/>, the option <see cref="ItemId"/>, by pressing <see cref="Key"/>.</summary>
public sealed record MenuChoice(int MenuId, int ItemId, int? Key, string? Title);

/// <summary>
/// The way a call went through a switchboard: which one it reached (call_started's to_smart_attendant_id) and the menu
/// options chosen on the way (entered_sa_menu), in order.
/// </summary>
public sealed record MenuJourney(int? SwitchboardId, IReadOnlyList<MenuChoice> Choices)
{
    /// <summary>From a call's events in order, each its name and data as Talk sent them.</summary>
    public static MenuJourney Of(IEnumerable<(string Event, string? Data)> events)
    {
        int? switchboard = null;
        var choices = new List<MenuChoice>();
        foreach (var (name, data) in events)
        {
            if (data is null || name is not ("call_started" or "entered_sa_menu"))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (name == "call_started")
            {
                switchboard ??= Int(root, "to_smart_attendant_id");
            }
            else if (Int(root, "sa_id") is { } menu && Int(root, "sa_item_id") is { } item)
            {
                choices.Add(new MenuChoice(menu, item, Int(root, "sa_item_key"),
                    root.TryGetProperty("sa_item_title", out var title) && title.ValueKind == JsonValueKind.String ? title.GetString() : null));
            }
        }

        return new MenuJourney(switchboard, choices);
    }

    private static int? Int(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;
}

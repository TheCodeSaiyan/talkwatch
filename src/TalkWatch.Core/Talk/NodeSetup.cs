using System.Globalization;
using System.Text.Json;

namespace TalkWatch.Core.Talk;

/// <summary>
/// What a switchboard node's JSON says beyond where it sits in the tree: its extension, whether it is switched off, its
/// greeting, what happens when nobody presses a key, dialling by extension, opening hours and who gets its voicemail.
/// Each is read from the JSON when asked for, and is null when this Talk does not send it, so 5.3.2 and 5.4.5 read
/// alike with no version checks.
/// </summary>
public readonly struct NodeSetup
{
    private readonly JsonElement node;

    private NodeSetup(JsonElement node) => this.node = node;

    /// <summary>A node's setup from its JSON as Talk sent it. Anything that is not an object reads as a node that says nothing.</summary>
    public static NodeSetup Of(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        using var document = JsonDocument.Parse(json);
        return new NodeSetup(document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : default);
    }

    /// <summary>The extension it can be dialled on, such as 0005.</summary>
    public string? Ext => Text("ext");

    /// <summary>Whether it is switched off in Talk.</summary>
    public bool? Disabled => Bool("disabled");

    /// <summary>The greeting or prompt it plays; null for a node that plays none (a group, user or contact).</summary>
    public Greeting? Greeting =>
        Has("greeting") || Has("greeting_file_name") || Has("greeting_type") || Has("missing_audio") || Has("generating_audio")
            ? new Greeting(Text("greeting"), Text("greeting_type"), Text("voice_type"), Text("voice_language"), Text("greeting_file_name"),
                Bool("generating_audio"), Bool("missing_audio"))
            : null;

    /// <summary>What happens when a caller presses nothing at a menu; null for a node with no menu.</summary>
    public NoKeyPress? NoKeyPress =>
        Has("sa_menu_noaction") || Has("noaction_transfer_to_node_id")
            ? new NoKeyPress(Text("sa_menu_noaction"), Text("noaction_transfer_to_node_id") ?? Number("noaction_transfer_to_node_id"),
                Text("noaction_transfer_to_parking_lot_uuid"), Text("noaction_vm_receiver_group_id") ?? Number("noaction_vm_receiver_group_id"),
                Ids("noaction_vm_receiver_uuids"))
            : null;

    /// <summary>Whether a caller may dial an extension from the switchboard, and which users and groups they may reach.</summary>
    public DialByExtension? DialByExt =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty("ext_dialing", out var e) && e.ValueKind == JsonValueKind.Object
            ? new DialByExtension(
                e.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True,
                e.TryGetProperty("target_scope", out var scope) && scope.ValueKind == JsonValueKind.String ? scope.GetString() : null,
                IdsOf(e, "target_user_group_ids") ?? [])
            : null;

    /// <summary>For a time node, the hours it covers; null for any other node.</summary>
    public OpeningHours? OpeningHours
    {
        get
        {
            if (Text("type") != "time")
            {
                return null;
            }

            var days = new Dictionary<DayOfWeek, IReadOnlyList<MinuteRange>>();
            if (node.TryGetProperty("day_ranges", out var ranges) && ranges.ValueKind == JsonValueKind.Object)
            {
                foreach (var day in ranges.EnumerateObject())
                {
                    // Talk numbers days as ISO does: 1 is Monday, 7 is Sunday.
                    if (!int.TryParse(day.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var iso) || iso is < 1 or > 7 || day.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    days[(DayOfWeek)(iso % 7)] = [.. day.Value.EnumerateArray()
                        .Where(r => r.ValueKind == JsonValueKind.Object)
                        .Select(r => (From: IntOf(r, "from"), To: IntOf(r, "to")))
                        .Where(r => r.From is not null && r.To is not null)
                        .Select(r => new MinuteRange(r.From!.Value, r.To!.Value))];
                }
            }

            return new OpeningHours(days, Bool("is_default_time_node") == true, Bool("holidays_enabled") == true, Holidays());
        }
    }

    /// <summary>For a group or user node, what Talk does with the call there, such as voicemail; null for ringing as usual.</summary>
    public string? CallAction => Text("call_action");

    /// <summary>
    /// Who gets the voicemail left here: a group's receivers, or a switchboard's receiver. Null when the node names none.
    /// Those for a caller who presses nothing are in <see cref="NoKeyPress"/>.
    /// </summary>
    public IReadOnlyList<string>? Receivers =>
        Ids("group_vm_receiver_uuids") ?? ((Text("vm_receiver") ?? Number("vm_receiver")) is { } one ? [one] : null);

    /// <summary>The number a contact node rings, when Talk names one rather than the contact's own.</summary>
    public string? TargetDid => Text("target_did");

    private List<DateOnly>? Holidays()
    {
        if (!node.TryGetProperty("holidays", out var holidays) || holidays.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        // No capture yet holds a holiday, so only the plainest shapes are read: a date, or an object with one. Anything
        // else leaves the dates unreadable (null), and the evaluator says Unknown rather than guess.
        var dates = new List<DateOnly>();
        foreach (var holiday in holidays.EnumerateArray())
        {
            var text = holiday.ValueKind == JsonValueKind.String ? holiday.GetString()
                : holiday.ValueKind == JsonValueKind.Object && holiday.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()
                : null;
            if (text is null || !DateOnly.TryParseExact(text.Length >= 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return null;
            }

            dates.Add(date);
        }

        return dates;
    }

    private bool Has(string name) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out _);

    private string? Text(string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string? Number(string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetRawText() : null;

    private bool? Bool(string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private IReadOnlyList<string>? Ids(string name) => node.ValueKind == JsonValueKind.Object ? IdsOf(node, name) : null;

    // Talk mixes numeric group ids and user uuids in one list.
    private static IReadOnlyList<string>? IdsOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(e => e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Number => e.GetRawText(),
                _ => null,
            }).OfType<string>()]
            : null;

    private static int? IntOf(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var i) ? i : null;
}

/// <summary>A node's greeting or prompt.</summary>
/// <param name="Text">What it says, for a greeting Talk generates from text.</param>
/// <param name="Type">generated, uploaded or default.</param>
/// <param name="Generating">Whether Talk is still making its audio.</param>
/// <param name="Missing">Whether its audio is missing; Talk 5.4.5 and later say, 5.3.2 does not (null).</param>
public sealed record Greeting(string? Text, string? Type, string? Voice, string? Language, string? FileName, bool? Generating, bool? Missing);

/// <summary>What happens when a caller presses nothing.</summary>
/// <param name="Action">Talk's name for it, such as drop_call or voicemail.</param>
public sealed record NoKeyPress(string? Action, string? TransferToNodeId, string? ParkingLotUuid, string? VoicemailGroupId, IReadOnlyList<string>? VoicemailReceivers);

/// <summary>Dialling an extension from a switchboard.</summary>
/// <param name="Scope">Who may be reached: allow_list limits it to <see cref="AllowList"/>.</param>
/// <param name="AllowList">The users (uuid) and groups (numeric id) a caller may reach.</param>
public sealed record DialByExtension(bool Enabled, string? Scope, IReadOnlyList<string> AllowList)
{
    public bool LimitedToAllowList => Scope == "allow_list";
}

/// <summary>Minutes after midnight. <see cref="To"/> at or before <see cref="From"/>, or past 1440, runs into the next day.</summary>
public readonly record struct MinuteRange(int From, int To);

/// <summary>A time node's hours.</summary>
/// <param name="IsDefault">The branch taken when no other time node beside it covers the moment: out of hours.</param>
/// <param name="Holidays">The days it is closed when <see cref="HolidaysEnabled"/>; null when Talk sent them in a shape TalkWatch cannot read.</param>
public sealed record OpeningHours(IReadOnlyDictionary<DayOfWeek, IReadOnlyList<MinuteRange>> Days, bool IsDefault, bool HolidaysEnabled, IReadOnlyList<DateOnly>? Holidays);

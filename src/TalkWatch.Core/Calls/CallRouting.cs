using System.Text.Json;
using TalkWatch.Core.Talk;

namespace TalkWatch.Core.Calls;

/// <summary>The kinds of thing a call can pass through, and so the kinds a grant can name.</summary>
public enum LineKind
{
    /// <summary>An external number on the account, in E.164.</summary>
    Did,

    /// <summary>A Talk user, by uuid. Their extension is a property of the user, not a separate line.</summary>
    User,

    RingGroup,

    /// <summary>A smart attendant (the switchboard or IVR menu).</summary>
    Attendant,

    Queue,

    /// <summary>An address-book contact a call was forwarded to, such as someone's mobile.</summary>
    Contact,
}

/// <summary>A line a call touched. Key is the E.164 number for a DID, otherwise Talk's id or uuid.</summary>
public readonly record struct LineRef(LineKind Kind, string Key);

public static class CallRouting
{
    /// <summary>
    /// Every line <paramref name="call"/> touched: the number it came in on or went out from, the attendant, group and
    /// queue it was routed through, every user and contact it tried or reached. A grant on any of them covers the call.
    /// </summary>
    public static IReadOnlySet<LineRef> TouchedLines(CallLogRecord call, NumberNormaliser numbers) => TouchedLines(call, numbers, LineDirectory.Empty);

    /// <summary>
    /// As above, and with the console's configuration: a call that came in on a DID routed to a ring group, or was
    /// dialled to a ring group's extension, went through that group. Lines are fixed when a call is stored, so the
    /// configuration used is the one in force then; later changes do not rewrite old calls.
    /// </summary>
    public static IReadOnlySet<LineRef> TouchedLines(CallLogRecord call, NumberNormaliser numbers, LineDirectory directory)
    {
        var lines = new HashSet<LineRef>();

        void Add(LineKind kind, string? key)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                lines.Add(new LineRef(kind, key));
            }
        }

        void AddDid(string? number)
        {
            if (numbers.ToE164(number) is { } e164)
            {
                lines.Add(new LineRef(LineKind.Did, e164));
            }
        }

        switch (call.Direction)
        {
            case "in":
                AddDid(call.To);
                break;
            case "out":
                AddDid(call.FromDid);
                break;
        }

        if (call.Direction == "in" && numbers.ToE164(call.To) is { } calledDid)
        {
            foreach (var group in directory.GroupsForDid(calledDid))
            {
                Add(LineKind.RingGroup, group);
            }
        }

        if (!string.IsNullOrWhiteSpace(call.To))
        {
            foreach (var group in directory.GroupsForExt(call.To))
            {
                Add(LineKind.RingGroup, group);
            }
        }

        Add(LineKind.Attendant, call.ToSmartAttendantId);
        Add(LineKind.RingGroup, call.ToGroupId);
        Add(LineKind.RingGroup, call.AnsweredByGroupId);
        Add(LineKind.Queue, call.ToQueueId);
        Add(LineKind.User, call.FromId);
        Add(LineKind.User, call.AnsweredByUserUuid);
        Add(LineKind.Contact, call.ToContactId);
        Add(LineKind.Contact, call.AnsweredByContactId);

        foreach (var e in call.CallEvents)
        {
            switch (e.Event)
            {
                case "call_started":
                    Add(LineKind.User, e.Text("from_user_uuid"));
                    Add(LineKind.Attendant, e.Text("to_smart_attendant_id"));
                    break;
                case "call_accepted":
                    Add(LineKind.Contact, e.Text("accepted_by_contact_id"));
                    break;

                // A call sent to someone's voicemail went through their line, whether or not a message was left.
                case "call_sent_to_voicemail" or "vm_msg_recorded" or "vm_recording_canceled":
                    foreach (var user in e.List("recipient_user_uuids"))
                    {
                        Add(LineKind.User, user);
                    }

                    break;
                case "seq_call_trying_endpoints":
                    foreach (var contact in e.List("contact_uuids"))
                    {
                        Add(LineKind.Contact, contact);
                    }

                    break;
                case "skipped_endpoints":
                    foreach (var user in e.List("user_uuids"))
                    {
                        Add(LineKind.User, user);
                    }

                    foreach (var contact in e.List("contact_uuids"))
                    {
                        Add(LineKind.Contact, contact);
                    }

                    foreach (var did in e.List("external_dids"))
                    {
                        AddDid(did);
                    }

                    break;
            }
        }

        return lines;
    }

    /// <summary>
    /// The outside contacts Talk put a call through to, by uuid, from its events in order: the contacts it rang
    /// (seq_call_trying_endpoints), and the one that answered. Contacts Talk skipped are left out: their phones never rang.
    /// Talk reports the ringing while the call is still going, so a call is known to be put through as it happens.
    /// </summary>
    public static IReadOnlyList<string> ContactsTried(IEnumerable<(string Event, string? Data)> events)
    {
        var contacts = new List<string>();
        foreach (var (name, data) in events)
        {
            if (data is null || name is not ("seq_call_trying_endpoints" or "call_accepted"))
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (name == "seq_call_trying_endpoints" && root.TryGetProperty("contact_uuids", out var tried) && tried.ValueKind == JsonValueKind.Array)
            {
                contacts.AddRange(tried.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String).Select(c => c.GetString()).OfType<string>());
            }
            else if (root.TryGetProperty("accepted_by_contact_uuid", out var answered) && answered.ValueKind == JsonValueKind.String && answered.GetString() is { } uuid)
            {
                contacts.Add(uuid);
            }
        }

        return [.. contacts.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Whether the call was answered, and if an outside contact answered it, which one: by its uuid and by the numeric id
    /// an answering line is kept under. Both null when someone on the phone system answered.
    /// </summary>
    public static (bool Answered, string? ContactUuid, string? ContactId) WhoAnswered(IEnumerable<(string Event, string? Data)> events)
    {
        foreach (var (name, data) in events)
        {
            if (name != "call_accepted")
            {
                continue;
            }

            if (data is null)
            {
                return (true, null, null);
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (true, null, null);
            }

            var uuid = root.TryGetProperty("accepted_by_contact_uuid", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var id = root.TryGetProperty("accepted_by_contact_id", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetRawText() : null;
            return (true, uuid, id);
        }

        return (false, null, null);
    }
}

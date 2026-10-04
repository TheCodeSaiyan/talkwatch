using System.Text.Json.Serialization;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Talk;

/// <summary>A ring group from <c>GET /proxy/talk/api/group_list</c>.</summary>
public sealed record TalkGroup
{
    /// <summary>Talk's numeric id: the key call records use in <c>to_group_id</c>.</summary>
    [JsonConverter(typeof(FlexibleIdConverter))]
    public required string Id { get; init; }

    public string? Uuid { get; init; }
    public string? Name { get; init; }
    public string? GroupType { get; init; }

    /// <summary>
    /// Whether calls ring it: a ring group, or a group whose kind Talk does not say. Paging groups, which only announce
    /// over handsets' speakers, never take a call.
    /// </summary>
    public bool TakesCalls => GroupType is null || string.Equals(GroupType, "ring_group", StringComparison.OrdinalIgnoreCase);

    /// <summary>Member uuids: users, and contacts when <see cref="MemberListMeta"/> says a member is not a user.</summary>
    public IReadOnlyList<string>? MemberList { get; init; }

    /// <summary>For each member, whether it is a Talk user or a contact (an outside number).</summary>
    public IReadOnlyList<Member>? MemberListMeta { get; init; }

    /// <summary>Where an unanswered call goes next: another ring group, a user or a contact.</summary>
    [JsonConverter(typeof(FlexibleIdConverter))]
    public string? TransferToGroupId { get; init; }

    public string? TransferToUserUuid { get; init; }
    public string? TransferToContactUuid { get; init; }

    /// <summary>Whether the member with this uuid is a contact rather than a Talk user.</summary>
    /// <summary>
    /// The outside numbers this group puts calls to, by contact uuid: members that are contacts, and the contact an
    /// unanswered call goes to next. Given the directory's contacts, since a member's kind is not always sent.
    /// </summary>
    public IEnumerable<string> ContactsIn(IReadOnlySet<string> contactUuids) =>
        (MemberList ?? []).Where(m => IsContact(m) || contactUuids.Contains(m))
            .Concat(TransferToContactUuid is { Length: > 0 } next ? [next] : [])
            .Distinct(StringComparer.Ordinal);

    public bool IsContact(string member) =>
        MemberListMeta?.FirstOrDefault(m => m.MemberId == member) is { GroupMemberData.MemberIsUser: false };

    public sealed record Member
    {
        public string? MemberId { get; init; }
        public MemberData? GroupMemberData { get; init; }
    }

    public sealed record MemberData
    {
        public bool? MemberIsUser { get; init; }
    }

    /// <summary>The group's own extensions: a call dialled to one of these went to the group.</summary>
    public IReadOnlyList<string>? ExtList { get; init; }

    /// <summary>External numbers routed straight to the group.</summary>
    public IReadOnlyList<string>? DidList { get; init; }
}

/// <summary>A number on the account from <c>GET /proxy/talk/api/number/list</c>, with the attendant it rings.</summary>
public sealed record TalkNumber
{
    public required string Did { get; init; }
    public AttendantNode? SmartAttendant { get; init; }

    /// <summary>The user the number rings directly, if it is assigned to one (uuid).</summary>
    public string? UserId { get; init; }

    /// <summary>The ring group the number rings directly, if any, as Talk sends it.</summary>
    public System.Text.Json.JsonElement? GroupData { get; init; }

    /// <summary>The id of the ring group in <see cref="GroupData"/>, or null.</summary>
    public string? GroupId => GroupData is { ValueKind: System.Text.Json.JsonValueKind.Object } g && g.TryGetProperty("id", out var id)
        ? id.ValueKind == System.Text.Json.JsonValueKind.Number ? id.GetRawText() : id.GetString()
        : null;

    public sealed record AttendantNode
    {
        [JsonConverter(typeof(FlexibleIdConverter))]
        public string? InternalId { get; init; }

        public string? Title { get; init; }
        public string? Ext { get; init; }
    }
}

/// <summary>
/// An entry in Talk's contacts (GET /proxy/talk/api/contacts): an outside person, such as a mobile a switchboard puts
/// calls through to, or a regular caller. Calls name a contact by its id in some fields and by its uuid in others.
/// </summary>
public sealed record TalkContact
{
    public required int Id { get; init; }
    public string? Uuid { get; init; }
    public string? FirstName { get; init; }
    public string? LastName { get; init; }
    public string? Organization { get; init; }
    public string? Email { get; init; }
    public IReadOnlyList<PhoneNumber>? PhoneNumbers { get; init; }

    /// <summary>The contact's email when Talk holds one that reads as an address; null otherwise.</summary>
    public string? Address => Email is { } e && e.Trim() is { Length: > 3 } t && t.Contains('@', StringComparison.Ordinal) ? t : null;

    public string DisplayName =>
        string.Join(' ', new[] { FirstName, LastName }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } name
            ? name
            : Organization is { Length: > 0 } organisation ? organisation : $"Contact {Id}";

    public sealed record PhoneNumber
    {
        public string? Did { get; init; }
        public string? Label { get; init; }
    }
}

/// <summary>
/// Every line the console knows about, by name: users, ring groups, numbers and attendants. Used to name lines on
/// screen and to recognise, from the console's own configuration, which calls went through which ring group.
/// </summary>
public sealed class LineDirectory
{
    public static readonly LineDirectory Empty = new([], [], []);

    /// <summary>Talk's contacts; empty when the console's account may not read them.</summary>
    public IReadOnlyList<TalkContact> Contacts { get; init; } = [];

    /// <summary>
    /// A name for each number in the contacts, in E.164: "Alex Smith (mobile)". A number two contacts share takes the
    /// first; a number that does not read as one is left out.
    /// </summary>
    public IReadOnlyDictionary<string, string> CallerNames(NumberNormaliser numbers)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var contact in Contacts)
        {
            foreach (var phone in contact.PhoneNumbers ?? [])
            {
                if (numbers.ToE164(phone.Did) is { } e164)
                {
                    names.TryAdd(e164, phone.Label is { Length: > 0 } label ? $"{contact.DisplayName} ({label})" : contact.DisplayName);
                }
            }
        }

        return names;
    }

    private readonly Dictionary<string, List<string>> _groupsByDid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<string>> _groupsByExt = new(StringComparer.Ordinal);

    public LineDirectory(IReadOnlyList<TalkUser> users, IReadOnlyList<TalkGroup> groups, IReadOnlyList<TalkNumber> numbers, NumberNormaliser? normaliser = null)
    {
        Users = users;
        Groups = groups;
        Numbers = numbers;
        normaliser ??= new NumberNormaliser("GB");
        foreach (var group in groups)
        {
            foreach (var did in group.DidList ?? [])
            {
                if (normaliser.ToE164(did) is { } e164)
                {
                    Add(_groupsByDid, e164, group.Id);
                }
            }

            foreach (var ext in group.ExtList ?? [])
            {
                Add(_groupsByExt, ext.Trim(), group.Id);
            }
        }
    }

    public IReadOnlyList<TalkUser> Users { get; }
    public IReadOnlyList<TalkGroup> Groups { get; }
    public IReadOnlyList<TalkNumber> Numbers { get; }

    /// <summary>The switchboard tree, once read; null when it has not been, or could not be.</summary>
    public IReadOnlyList<SwitchboardNode>? Switchboard { get; set; }

    /// <summary>
    /// Who a call to one of the account's numbers can reach: the people an operator hands calls to, and the contacts
    /// (outside numbers) calls are put through to. A user no number reaches, such as a console administrator, is not
    /// one of them, and nor is a contact, however Talk lists it. When the routing cannot be read in full (numbers go
    /// to a switchboard that has not been read), every listed user counts: nobody is hidden for want of the data.
    /// </summary>
    public Reach Reachable(string? did = null, NumberNormaliser? numbers = null)
    {
        // One number (E.164) when the board is narrowed to it: only what a call to that number can reach.
        bool Mine(string? number) => did is null || (number is { Length: > 0 } n && ((numbers ?? new NumberNormaliser("GB")).ToE164(n) ?? n) == did);
        var contactUuids = Contacts.Select(c => c.Uuid).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var listed = Users.Where(u => !u.HideFromUserList && !contactUuids.Contains(u.Uuid)).ToList();
        if (Switchboard is null && Numbers.Any(n => n.SmartAttendant is not null))
        {
            return new Reach(listed.Select(u => u.Uuid).ToHashSet(StringComparer.Ordinal), [], Complete: false);
        }

        var users = new HashSet<string>(StringComparer.Ordinal);
        var contacts = new HashSet<string>(StringComparer.Ordinal);
        var groups = new Queue<string>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        void Group(string? id)
        {
            if (!string.IsNullOrWhiteSpace(id) && seenGroups.Add(id))
            {
                groups.Enqueue(id);
            }
        }

        users.UnionWith(listed.Where(u => did is null ? u.HasOwnNumber : Mine(u.Did)).Select(u => u.Uuid));
        foreach (var group in Groups.Where(g => g.DidList is { Count: > 0 } list && (did is null || list.Any(Mine))))
        {
            Group(group.Id);
        }

        var nodes = Switchboard ?? [];
        var roots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var number in Numbers.Where(n => Mine(n.Did)))
        {
            if (number.UserId is { Length: > 0 } user)
            {
                users.Add(user);
            }

            Group(number.GroupId);
            if (number.SmartAttendant?.InternalId is { } attendant)
            {
                roots.UnionWith(nodes.Where(n => n.Type == SwitchboardNode.Root && n.InternalId?.ToString(System.Globalization.CultureInfo.InvariantCulture) == attendant).Select(n => n.Id));
            }
        }

        // A switchboard answering a number reaches everything under it.
        roots.UnionWith(nodes.Where(n => n.Type == SwitchboardNode.Root && n.Numbers.Count > 0 && (did is null || n.Numbers.Any(Mine))).Select(n => n.Id));
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        bool Under(SwitchboardNode node)
        {
            for (var (at, steps) = (node, 0); at is not null && steps < 64; at = at.ParentId is { } p ? byId.GetValueOrDefault(p) : null, steps++)
            {
                if (roots.Contains(at.Id))
                {
                    return true;
                }
            }

            return false;
        }

        foreach (var node in nodes.Where(Under))
        {
            Group(node.GroupId);
            if (node.UserUuid is { Length: > 0 } user)
            {
                users.Add(user);
            }

            if (node.ContactUuid is { Length: > 0 } contact)
            {
                contacts.Add(contact);
            }
        }

        var byGroupId = Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        while (groups.TryDequeue(out var id))
        {
            if (!byGroupId.TryGetValue(id, out var group))
            {
                continue;
            }

            foreach (var member in group.MemberList ?? [])
            {
                (group.IsContact(member) || contactUuids.Contains(member) ? contacts : users).Add(member);
            }

            Group(group.TransferToGroupId);
            if (group.TransferToUserUuid is { Length: > 0 } user)
            {
                users.Add(user);
            }

            if (group.TransferToContactUuid is { Length: > 0 } contact)
            {
                contacts.Add(contact);
            }
        }

        return new Reach(
            listed.Where(u => users.Contains(u.Uuid)).Select(u => u.Uuid).ToHashSet(StringComparer.Ordinal),
            [.. Contacts.Where(c => c.Uuid is { } uuid && contacts.Contains(uuid))],
            Complete: true) { Groups = seenGroups };
    }

    /// <summary>
    /// For each of the account's numbers (E.164), the lines a call to it is routed through: the number itself, the
    /// switchboards that answer it and the ring groups it rings, directly, from a switchboard, or as where an unanswered
    /// group call goes next. This is what a number covers as an access boundary. People are deliberately not in it: one
    /// person can be behind several numbers, and following them would carry one number's calls into another's.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<LineRef>> Routes(NumberNormaliser numbers)
    {
        var nodes = Switchboard ?? [];
        var byId = nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        var byGroupId = Groups.ToDictionary(g => g.Id, StringComparer.Ordinal);
        var dids = Numbers.Select(n => numbers.ToE164(n.Did)).OfType<string>()
            .Concat(Groups.SelectMany(g => g.DidList ?? []).Select(d => numbers.ToE164(d)).OfType<string>())
            .Concat(nodes.SelectMany(n => n.Numbers).Select(d => numbers.ToE164(d)).OfType<string>())
            .ToHashSet(StringComparer.Ordinal);

        var routes = new Dictionary<string, IReadOnlySet<LineRef>>(StringComparer.Ordinal);
        foreach (var did in dids)
        {
            var lines = new HashSet<LineRef> { new(LineKind.Did, did) };
            var groups = new Queue<string>(GroupsForDid(did));
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var number in Numbers.Where(n => numbers.ToE164(n.Did) == did))
            {
                if (number.GroupId is { Length: > 0 } group)
                {
                    groups.Enqueue(group);
                }

                if (number.SmartAttendant?.InternalId is { } attendant)
                {
                    lines.Add(new LineRef(LineKind.Attendant, attendant));
                    roots.UnionWith(nodes.Where(n => n.Type == SwitchboardNode.Root && n.InternalId?.ToString(System.Globalization.CultureInfo.InvariantCulture) == attendant).Select(n => n.Id));
                }
            }

            foreach (var root in nodes.Where(n => n.Type == SwitchboardNode.Root && n.Numbers.Any(d => numbers.ToE164(d) == did)))
            {
                roots.Add(root.Id);
                if (root.InternalId is { } id)
                {
                    lines.Add(new LineRef(LineKind.Attendant, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }

            foreach (var node in nodes.Where(n => n.GroupId is not null && Under(n, roots, byId)))
            {
                groups.Enqueue(node.GroupId!);
            }

            while (groups.TryDequeue(out var id))
            {
                if (lines.Add(new LineRef(LineKind.RingGroup, id)) && byGroupId.TryGetValue(id, out var group) && group.TransferToGroupId is { Length: > 0 } next)
                {
                    groups.Enqueue(next);
                }
            }

            routes[did] = lines;
        }

        return routes;
    }

    private static bool Under(SwitchboardNode node, HashSet<string> roots, Dictionary<string, SwitchboardNode> byId)
    {
        for (var (at, steps) = (node, 0); at is not null && steps < 64; at = at.ParentId is { } p ? byId.GetValueOrDefault(p) : null, steps++)
        {
            if (roots.Contains(at.Id))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What <see cref="Reachable"/> found.</summary>
    /// <param name="Users">The uuids of Talk users a number reaches.</param>
    /// <param name="Contacts">The contacts a number reaches.</param>
    /// <param name="Complete">False when the routing could not be read and every listed user was kept.</param>
    public sealed record Reach(IReadOnlySet<string> Users, IReadOnlyList<TalkContact> Contacts, bool Complete)
    {
        /// <summary>The groups a call can ring, by Talk's id: directly, from a switchboard, or where an unanswered one goes next.</summary>
        public IReadOnlySet<string> Groups { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Ring groups a DID (E.164) is routed to.</summary>
    public IReadOnlyList<string> GroupsForDid(string e164) => _groupsByDid.GetValueOrDefault(e164) ?? [];

    /// <summary>Ring groups with this extension.</summary>
    public IReadOnlyList<string> GroupsForExt(string ext) => _groupsByExt.GetValueOrDefault(ext.Trim()) ?? [];

    /// <summary>Every line with a display name: what a grants screen offers, and how calls show who was involved.</summary>
    public IEnumerable<(LineKind Kind, string Key, string Name, string? Ext)> Lines(NumberNormaliser numbers)
    {
        foreach (var user in Users.Where(u => !u.HideFromUserList))
        {
            yield return (LineKind.User, user.Uuid, user.DisplayName, user.Ext);
        }

        foreach (var group in Groups)
        {
            yield return (LineKind.RingGroup, group.Id, group.Name ?? $"Ring group {group.Id}", group.ExtList is { Count: > 0 } exts ? exts[0] : null);
        }

        foreach (var number in Numbers)
        {
            if (numbers.ToE164(number.Did) is { } e164)
            {
                yield return (LineKind.Did, e164, e164, null);
            }

            if (number.SmartAttendant is { InternalId: { } attendant } node)
            {
                yield return (LineKind.Attendant, attendant, node.Title ?? $"Attendant {attendant}", node.Ext);
            }
        }

        // Calls name a contact by id (to_contact_id) or by uuid (the endpoints a call rang): one name for both.
        foreach (var contact in Contacts)
        {
            yield return (LineKind.Contact, contact.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), contact.DisplayName, null);
            if (contact.Uuid is { Length: > 0 } uuid)
            {
                yield return (LineKind.Contact, uuid, contact.DisplayName, null);
            }
        }
    }

    private static void Add(Dictionary<string, List<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        if (!list.Contains(value, StringComparer.Ordinal))
        {
            list.Add(value);
        }
    }
}

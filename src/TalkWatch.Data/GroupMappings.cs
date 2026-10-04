using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>
/// What a group in the identity provider gives its members when they sign in through it: a role, lines, or both. The
/// first mapping in order that names a role decides it; the lines of every mapping someone's groups match are theirs.
/// </summary>
public sealed class GroupMapping
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>The group's name as the provider sends it in the groups claim.</summary>
    public required string Group { get; set; }

    /// <summary>The role it gives, or null for lines only.</summary>
    public string? Role { get; set; }

    /// <summary>Lower first: where someone's groups name different roles, the first mapping's wins.</summary>
    public int Order { get; set; }

    public List<GroupMappingLine> Lines { get; set; } = [];
}

/// <summary>A line a group's members are granted, with the same ticks a grant has.</summary>
public sealed class GroupMappingLine
{
    public Guid Id { get; set; }
    public Guid MappingId { get; set; }
    public LineKind Kind { get; set; }
    public required string Key { get; set; }
    public bool AllowRecordings { get; set; }
    public bool AllowVoicemail { get; set; }
    public bool AllowTranscripts { get; set; }
}

/// <summary>What someone's groups give them, worked out from the mappings and the Oidc__ settings.</summary>
/// <param name="Allowed">Whether they may sign in at all.</param>
/// <param name="Role">The role their groups set, or null when they set none and the account keeps what it has.</param>
/// <param name="Lines">The lines their groups grant, one each, ticks combined.</param>
public sealed record GroupAccess(bool Allowed, string? Role, IReadOnlyList<GroupLine> Lines)
{
    /// <summary>
    /// Admin groups make an Admin whatever else is mapped, so a mistake on the mappings page cannot lock admins out.
    /// Then the first mapping in order that names a role. A group mapped to lines only, or a viewer group, lets someone
    /// in without setting a role. Anyone their groups match nothing for is turned away.
    /// </summary>
    public static GroupAccess Of(IReadOnlySet<string> groups, IEnumerable<GroupMapping> mappings, IReadOnlySet<string> adminGroups, IReadOnlySet<string> viewerGroups)
    {
        var matched = mappings.Where(m => groups.Contains(m.Group)).OrderBy(m => m.Order).ThenBy(m => m.Group, StringComparer.Ordinal).ToList();
        var admin = groups.Overlaps(adminGroups);
        var allowed = admin || matched.Count > 0 || groups.Overlaps(viewerGroups);
        var role = admin ? Roles.Admin : matched.FirstOrDefault(m => !string.IsNullOrEmpty(m.Role))?.Role;
        var lines = matched.SelectMany(m => m.Lines.Select(l => (Mapping: m, Line: l)))
            .GroupBy(x => (x.Line.Kind, x.Line.Key))
            .Select(g => new GroupLine(g.Key.Kind, g.Key.Key,
                g.Any(x => x.Line.AllowRecordings), g.Any(x => x.Line.AllowVoicemail), g.Any(x => x.Line.AllowTranscripts),
                string.Join(",", g.Select(x => x.Mapping.Group).Distinct())))
            .ToList();
        return new GroupAccess(allowed, role, lines);
    }
}

/// <summary>A line someone's groups grant, ticks combined across them, and which groups gave it.</summary>
public sealed record GroupLine(LineKind Kind, string Key, bool AllowRecordings, bool AllowVoicemail, bool AllowTranscripts, string Groups);

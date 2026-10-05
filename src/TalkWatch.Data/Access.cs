using Microsoft.AspNetCore.Identity;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>
/// Who a database context is working for. Every query on call data is filtered by it, in the query layer, so no screen,
/// export or API can forget to check.
/// </summary>
/// <remarks>
/// The default is <see cref="Nobody"/>, which sees nothing: a context someone forgot to give a scope fails closed.
/// </remarks>
public sealed record AccessScope(Guid SiteId, Guid? UserId, Permission Permissions)
{
    /// <summary>Sees nothing. What a context has until it is told otherwise.</summary>
    public static readonly AccessScope Nobody = new(Guid.Empty, null, Permission.None);

    /// <summary>TalkWatch itself, for ingestion and retention sweeps: everything in one site.</summary>
    public static AccessScope System(Guid siteId) => new(siteId, null, Permission.All);

    /// <summary>A signed-in person, with what their role allows; beyond that, they see what their grants cover.</summary>
    public static AccessScope ForUser(Guid siteId, Guid userId, Permission permissions) => new(siteId, userId, permissions);

    /// <summary>A signed-in person who is an admin, or one with only their grants.</summary>
    public static AccessScope ForUser(Guid siteId, Guid userId, bool isAdmin) => new(siteId, userId, isAdmin ? Permission.All : Permission.None);

    /// <summary>
    /// The one number (a DID, as E.164) this person has chosen to look at, or null for all of them. Narrows calls on top
    /// of what they may see; it is a view, not a permission, and never widens anything.
    /// </summary>
    public string? Did { get; init; }

    /// <summary>
    /// The numbers a report copy covers (E.164): only calls on them, or through what they route to. Null for no such
    /// narrowing. Like <see cref="Did"/>, it narrows and never widens.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819", Justification = "Handed to the query filters as a parameter array, which EF translates.")]
    public string[]? Numbers { get; init; }

    /// <summary>
    /// The calls a report copy counts when it covers only chosen hours: those in its window. Null for no such narrowing;
    /// like <see cref="Numbers"/>, it narrows and never widens.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1819", Justification = "Handed to the query filters as a parameter array, which EF translates.")]
    public Guid[]? OnlyCalls { get; init; }

    public bool Can(Permission permission) => (Permissions & permission) == permission;
}

/// <summary>
/// What a role allows, beyond the lines a person is granted. A role holds any set of these; Admin holds them all. Each
/// is a role claim (<see cref="Permissions.ClaimType"/>) named as here, so it travels in the sign-in cookie.
/// </summary>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "The suffix was for code access security, long gone; permission is the word people use.")]
public enum Permission
{
    None = 0,

    /// <summary>Every line's calls, lines and figures, not just granted ones.</summary>
    AllCalls = 1 << 0,

    /// <summary>Every recording and voicemail, without a grant's ticks.</summary>
    AllAudio = 1 << 1,

    /// <summary>Every transcript, without a grant's tick.</summary>
    AllTranscripts = 1 << 2,

    /// <summary>The audit log: who changed, played, read and exported what.</summary>
    ReadAudit = 1 << 3,

    /// <summary>Download calls as CSV or Parquet.</summary>
    Export = 1 << 4,

    /// <summary>Make personal API tokens.</summary>
    ApiTokens = 1 << 5,

    /// <summary>Mark missed callers done on the call-back list.</summary>
    MarkCallBacks = 1 << 6,

    /// <summary>Set up alert channels and flows of one's own, for one's own lines.</summary>
    OwnAlerts = 1 << 7,

    /// <summary>Every alert channel and flow, the mail and Telegram settings, and the alert about TalkWatch itself.</summary>
    ManageAlerts = 1 << 8,

    /// <summary>People, their roles and grants, roles themselves, and group mappings.</summary>
    ManagePeople = 1 << 9,

    /// <summary>How long calls and audio are kept.</summary>
    ManageRetention = 1 << 10,

    /// <summary>Set up reports and their schedules, and read every copy. Anyone reads the copies sent to them.</summary>
    ManageReports = 1 << 11,

    /// <summary>Who holds which role on a number. Held on a number, it covers that number only.</summary>
    ManageNumberPeople = 1 << 12,

    All = AllCalls | AllAudio | AllTranscripts | ReadAudit | Export | ApiTokens | MarkCallBacks | OwnAlerts | ManageAlerts | ManagePeople | ManageRetention | ManageReports | ManageNumberPeople,
}

public static class Permissions
{
    /// <summary>The role claim that holds one permission, by name.</summary>
    public const string ClaimType = "talkwatch:permission";

    /// <summary>Every single permission, in the order the roles page lists them.</summary>
    public static readonly Permission[] Each = [.. Enum.GetValues<Permission>().Where(p => p is not Permission.None and not Permission.All)];

    /// <summary>What the built-in roles had before roles held permissions, so upgrading changes nothing.</summary>
    public static Permission DefaultFor(string role) => role switch
    {
        Roles.Admin => Permission.All,
        Roles.Manager => Permission.Export | Permission.ApiTokens | Permission.MarkCallBacks | Permission.OwnAlerts,
        Roles.Viewer => Permission.Export | Permission.ApiTokens | Permission.MarkCallBacks,
        _ => Permission.None,
    };

    /// <summary>A person's permissions, from their claims.</summary>
    public static Permission Of(System.Security.Claims.ClaimsPrincipal user) =>
        user.FindAll(ClaimType).Aggregate(Permission.None, (all, claim) => Enum.TryParse<Permission>(claim.Value, out var p) && Each.Contains(p) ? all | p : all);

    public static bool Can(this System.Security.Claims.ClaimsPrincipal user, Permission permission) => (Of(user) & permission) == permission;

    /// <summary>The authorisation policy that requires one permission.</summary>
    public static string Policy(Permission permission) => $"permission:{permission}";

    /// <summary>The policy for the alerts pages: one's own alerts, or every alert.</summary>
    public const string AlertsPolicy = "permission:alerts";

    /// <summary>The policy for what only the Admin role may do, whatever permissions another role holds: the console's credentials and the way to it.</summary>
    public const string AdminPolicy = "role:admin";

    /// <summary>
    /// What a role allows when it is held on a number rather than site-wide: about that number's calls, never about the
    /// site. Anything else a role holds is ignored on a number.
    /// </summary>
    public const Permission PerNumber = Permission.AllAudio | Permission.AllTranscripts | Permission.Export | Permission.MarkCallBacks
        | Permission.OwnAlerts | Permission.ManageNumberPeople | Permission.ManageReports;

    /// <summary>The policy for setting reports up: site-wide, or for a number one holds the permission on.</summary>
    public const string ReportsPolicy = "permission:reports";

    public static string Describe(Permission permission) => permission switch
    {
        Permission.AllCalls => "See every line's calls, not only granted ones",
        Permission.AllAudio => "Hear every recording and voicemail",
        Permission.AllTranscripts => "Read every transcript",
        Permission.ReadAudit => "Read the audit log",
        Permission.Export => "Export calls as CSV or Parquet",
        Permission.ApiTokens => "Make API tokens",
        Permission.MarkCallBacks => "Mark missed callers done",
        Permission.OwnAlerts => "Set up their own alerts, for their own lines",
        Permission.ManageAlerts => "Manage every alert, and the alert settings",
        Permission.ManagePeople => "Manage people, roles, grants and group mappings",
        Permission.ManageRetention => "Manage retention",
        Permission.ManageReports => "Set up reports, and read every copy",
        Permission.ManageNumberPeople => "Choose who holds which role on a number",
        _ => permission.ToString(),
    };
}

/// <summary>Where a context gets its scope from, asked each time a query runs.</summary>
public interface IAccessScopeSource
{
    AccessScope Current { get; }
}

/// <summary>A scope that never changes: background work, tests, and the EF tools.</summary>
public sealed class FixedAccessScope(AccessScope scope) : IAccessScopeSource
{
    public AccessScope Current => scope;
}

/// <summary>
/// The built-in roles. Admin holds every permission and cannot be changed; Manager and Viewer start with what they
/// always had and can be changed, and admins can add roles of their own.
/// </summary>
public static class Roles
{
    /// <summary>Everything, including users, grants and settings.</summary>
    public const string Admin = "Admin";

    /// <summary>Their granted lines, plus managing alerts for them.</summary>
    public const string Manager = "Manager";

    /// <summary>Their granted lines.</summary>
    public const string Viewer = "Viewer";

    public static readonly string[] BuiltIn = [Admin, Manager, Viewer];
}

public sealed class AppUser : IdentityUser<Guid>
{
    public Guid SiteId { get; set; }

    /// <summary>
    /// Which Talk user this person is on the phone system, if they are one: what lets a flow reach whoever in a ring
    /// group is free. Set by an admin; null for people who only use TalkWatch.
    /// </summary>
    public string? TalkUserUuid { get; set; }

    /// <summary>
    /// The number (a DID, as E.164) this person has chosen in the rail's switcher, or null for all numbers. Kept on the
    /// account, so the choice follows them to every device; every call page narrows to it.
    /// </summary>
    public string? ContextDid { get; set; }

    /// <summary>
    /// Where this person's reports go, when not to their account email: a shared inbox, say, or a work address when
    /// single sign-on gives a personal one. Set by an admin, and never touched by a sign-in.
    /// </summary>
    public string? ReportEmail { get; set; }

    /// <summary>Where their reports go: the reporting address when there is one, else their email; null when neither is set.</summary>
    public string? ReportAddress => ReportTo(ReportEmail, Email);

    /// <summary>The reporting address when there is one, else the email; for a query that selected the two.</summary>
    public static string? ReportTo(string? reportEmail, string? email) =>
        !string.IsNullOrWhiteSpace(reportEmail) ? reportEmail.Trim() : string.IsNullOrWhiteSpace(email) ? null : email.Trim();
}

/// <summary>
/// A role a person holds on one of the account's numbers. They see that number's calls, those on the DID and those
/// through the switchboards and ring groups it routes to, and the role's per-number permissions
/// (<see cref="Permissions.PerNumber"/>) apply to those calls only. Apart from any site-wide role and grants.
/// </summary>
public sealed class NumberRole
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid UserId { get; set; }

    /// <summary>The number, as E.164.</summary>
    public required string Did { get; set; }

    public Guid RoleId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

/// <summary>
/// A line a number routes calls through: itself, a switchboard answering it, or a ring group it rings. Kept from the
/// console's configuration with the directory, so a number's role covers the calls passed on inside it.
/// </summary>
public sealed class NumberRoute
{
    public Guid SiteId { get; set; }
    public required string Did { get; set; }
    public LineKind Kind { get; set; }
    public required string Key { get; set; }
}

/// <summary>
/// Lets a person see the calls that touched one line: a DID, user, ring group, attendant, queue or contact. A call is
/// visible when any line it touched is granted. The content flags each open one more sensitive kind of data on
/// those calls, and are separate because hearing a recording is a bigger step than seeing that a call happened.
/// </summary>
public sealed class Grant
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid UserId { get; set; }
    public LineKind Kind { get; set; }
    public required string Key { get; set; }

    public bool AllowRecordings { get; set; }
    public bool AllowVoicemail { get; set; }
    public bool AllowTranscripts { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    /// <summary>
    /// Given by the person's groups at sign-in, not by hand: kept in step with their groups at every sign-in, and never
    /// changed on their page. A line can have one of each.
    /// </summary>
    public bool ByGroups { get; set; }

    /// <summary>For a grant by groups, which groups gave it, comma-separated.</summary>
    public string? Groups { get; set; }
}

using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// How far someone who manages people may go. An admin may do anything; anyone else hands out no more than they hold
/// themselves: not the Admin role, not a permission their own role lacks, not a line they cannot see or hear, and not
/// the keys to an account that reaches further than theirs. Without it, managing people was a way to become an admin.
/// </summary>
public sealed class PeopleReach
{
    private readonly bool _admin;
    private readonly Permission _permissions;
    private readonly List<Grant> _mine;
    private readonly TalkWatchDbContext _db;
    private readonly UserManager<AppUser> _users;
    private readonly RoleManager<IdentityRole<Guid>> _roles;

    private PeopleReach(bool admin, Permission permissions, List<Grant> mine, TalkWatchDbContext db, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles) =>
        (_admin, _permissions, _mine, _db, _users, _roles) = (admin, permissions, mine, db, users, roles);

    public static async Task<PeopleReach> ForAsync(HttpContext http)
    {
        var services = http.RequestServices;
        var db = services.GetRequiredService<TalkWatchDbContext>();
        var me = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;
        var admin = http.User.IsInRole(Roles.Admin);
        var mine = admin ? [] : await db.Grants.Where(g => g.UserId == me).ToListAsync(http.RequestAborted);
        return new PeopleReach(admin, Permissions.Of(http.User), mine, db,
            services.GetRequiredService<UserManager<AppUser>>(), services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
    }

    /// <summary>Why these permissions may not be given, or null when they may.</summary>
    public string? Give(Permission wanted) =>
        _admin || (wanted & ~_permissions) == Permission.None
            ? null
            : $"You can only give permissions you hold yourself, so not: {Describe(wanted & ~_permissions)}.";

    /// <summary>Why this role may not be given to someone, or null when it may.</summary>
    public async Task<string?> RoleAsync(string? name)
    {
        if (_admin || string.IsNullOrEmpty(name))
        {
            return null;
        }

        if (name == Roles.Admin)
        {
            return "Only an admin can make someone an admin.";
        }

        return await _roles.FindByNameAsync(name) is { } role ? Give(await RolePermissions.GetAsync(_roles, role)) : null;
    }

    /// <summary>Why this line, with these ticks, may not be granted, or null when it may.</summary>
    public string? Line(LineKind kind, string key, bool recordings, bool voicemail, bool transcripts)
    {
        if (_admin)
        {
            return null;
        }

        var mine = _mine.Where(g => g.Kind == kind && g.Key == key).ToList();
        var sees = _permissions.HasFlag(Permission.AllCalls) || mine.Count > 0;
        var hears = _permissions.HasFlag(Permission.AllAudio);
        var reads = _permissions.HasFlag(Permission.AllTranscripts);
        return sees
            && (!recordings || hears || mine.Any(g => g.AllowRecordings))
            && (!voicemail || hears || mine.Any(g => g.AllowVoicemail))
            && (!transcripts || reads || mine.Any(g => g.AllowTranscripts))
            ? null
            : "You can only grant a line you can see yourself, with no more than you can hear and read on it.";
    }

    /// <summary>
    /// Why this account may not be changed, or null when it may: anyone can change what reaches no further than they
    /// do, and only an admin can change an admin, or anyone else whose role, lines or numbers go beyond theirs.
    /// </summary>
    public async Task<string?> AccountAsync(AppUser user)
    {
        if (_admin)
        {
            return null;
        }

        var refusal = "Only an admin can change the account of someone who can do or see more than you can.";
        var roles = await _users.GetRolesAsync(user);
        if (roles.Contains(Roles.Admin))
        {
            return refusal;
        }

        foreach (var name in roles)
        {
            if (await _roles.FindByNameAsync(name) is { } role && Give(await RolePermissions.GetAsync(_roles, role)) is not null)
            {
                return refusal;
            }
        }

        var grants = await _db.Grants.Where(g => g.UserId == user.Id).ToListAsync();
        if (grants.Any(g => Line(g.Kind, g.Key, g.AllowRecordings, g.AllowVoicemail, g.AllowTranscripts) is not null))
        {
            return refusal;
        }

        // A role on a number reaches that number's calls and, by its permissions, its audio and transcripts.
        const Permission everything = Permission.AllCalls | Permission.AllAudio | Permission.AllTranscripts;
        return (_permissions & everything) != everything && await _db.NumberRoles.AnyAsync(n => n.UserId == user.Id) ? refusal : null;
    }

    private static string Describe(Permission permissions) =>
        string.Join(", ", Permissions.Each.Where(p => permissions.HasFlag(p)).Select(Permissions.Describe).Select(d => d.ToLowerInvariant()));
}

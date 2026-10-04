using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>
/// What a person's roles on numbers allow, for the checks the query filters cannot make alone: whether they may export
/// or mark call-backs, and on which calls. Read live, like grants, so taking a role away takes effect at once.
/// </summary>
public static class NumberAccess
{
    /// <summary>The numbers on which the person holds a role allowing <paramref name="permission"/>; any role for None.</summary>
    public static IQueryable<string> NumbersWith(TalkWatchDbContext db, Guid userId, Permission permission)
    {
        var roles = db.NumberRoles.Where(n => n.UserId == userId);
        if (permission == Permission.None)
        {
            return roles.Select(n => n.Did);
        }

        if ((Permissions.PerNumber & permission) != permission || !Permissions.Each.Contains(permission))
        {
            throw new ArgumentException($"{permission} is not a single permission a role can hold on a number.", nameof(permission));
        }

        var name = permission.ToString();
        return roles.Where(n => db.Set<IdentityRoleClaim<Guid>>().Any(c => c.RoleId == n.RoleId && c.ClaimType == Permissions.ClaimType && c.ClaimValue == name))
            .Select(n => n.Did);
    }

    /// <summary>Whether the person holds a role allowing <paramref name="permission"/> on any number.</summary>
    public static Task<bool> AnywhereAsync(TalkWatchDbContext db, Guid userId, Permission permission, CancellationToken cancellationToken = default) =>
        NumbersWith(db, userId, permission).AnyAsync(cancellationToken);

    /// <summary>Whether the person holds a role allowing <paramref name="permission"/> on this number.</summary>
    public static Task<bool> OnNumberAsync(TalkWatchDbContext db, Guid userId, string did, Permission permission, CancellationToken cancellationToken = default) =>
        NumbersWith(db, userId, permission).AnyAsync(d => d == did, cancellationToken);

    /// <summary>
    /// Only the calls these numbers cover: those on one of the DIDs, and those through a switchboard or ring group one of
    /// them routes calls to.
    /// </summary>
    public static IQueryable<CallRow> OnNumbers(this IQueryable<CallRow> calls, TalkWatchDbContext db, IQueryable<string> dids) =>
        calls.Where(c => db.CallLines.Any(l => l.CallId == c.Id
            && ((l.Kind == LineKind.Did && dids.Contains(l.Key)) || db.NumberRoutes.Any(r => dids.Contains(r.Did) && r.Kind == l.Kind && r.Key == l.Key))));
}

using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// A person's permissions, read from their roles on every request rather than kept from when they signed in: a change to
/// their role, or to a role's permissions, holds from their next page, and nobody is signed out for it. Everything that
/// reads permissions from the principal (the policies, the access scope, the pages) sees the current ones.
/// </summary>
public sealed class LivePermissions(TalkWatchDbContext db) : IClaimsTransformation
{
    private const string Read = "talkwatch:permissions-read";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity
            || principal.HasClaim(c => c.Type == Read)
            || !Guid.TryParse(principal.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
        {
            return principal;
        }

        var current = await (
            from ur in db.UserRoles
            join rc in db.RoleClaims on ur.RoleId equals rc.RoleId
            where ur.UserId == userId && rc.ClaimType == Permissions.ClaimType
            select rc.ClaimValue).Distinct().ToListAsync();

        var fresh = identity.Clone();
        foreach (var old in fresh.FindAll(Permissions.ClaimType).ToList())
        {
            fresh.RemoveClaim(old);
        }

        fresh.AddClaims(current.OfType<string>().Select(p => new Claim(Permissions.ClaimType, p)));
        fresh.AddClaim(new Claim(Read, "1"));
        return new ClaimsPrincipal(fresh);
    }
}

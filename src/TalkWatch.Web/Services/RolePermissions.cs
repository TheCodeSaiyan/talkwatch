using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// The permissions a role holds, as role claims. Admin always holds them all. Manager and Viewer are given what they
/// had before roles held permissions, once, and are then the admins' to change; roles admins add start with none.
/// </summary>
public static class RolePermissions
{
    /// <summary>Marks a built-in role as given its starting permissions, so emptying it later is not undone at start-up.</summary>
    private const string Seeded = "talkwatch:seeded";

    public static async Task SeedAsync(RoleManager<IdentityRole<Guid>> roles, IdentityRole<Guid> role)
    {
        var claims = await roles.GetClaimsAsync(role);
        if (role.Name == Roles.Admin)
        {
            // Admin is everything, always: added back if a permission is ever missing, such as a new one.
            await SetAsync(roles, role, claims, Permission.All);
        }
        else if (!claims.Any(c => c.Type == Seeded))
        {
            await SetAsync(roles, role, claims, Permissions.DefaultFor(role.Name ?? ""));
            await roles.AddClaimAsync(role, new Claim(Seeded, "1"));
        }
    }

    public static async Task<Permission> GetAsync(RoleManager<IdentityRole<Guid>> roles, IdentityRole<Guid> role) =>
        Of(await roles.GetClaimsAsync(role));

    public static Permission Of(IEnumerable<Claim> claims) =>
        claims.Where(c => c.Type == Permissions.ClaimType)
            .Aggregate(Permission.None, (all, c) => Enum.TryParse<Permission>(c.Value, out var p) && Permissions.Each.Contains(p) ? all | p : all);

    /// <summary>
    /// Gives a role exactly these permissions, and has its members' sign-ins pick them up within a minute. Admin's
    /// cannot be changed.
    /// </summary>
    public static async Task ChangeAsync(RoleManager<IdentityRole<Guid>> roles, IdentityRole<Guid> role, Permission permissions)
    {
        if (role.Name == Roles.Admin)
        {
            throw new InvalidOperationException("Admin holds every permission and cannot be changed.");
        }

        // Its members' next pages have the new permissions: they are read from the role on every request (LivePermissions),
        // so nobody needs signing out for it.
        await SetAsync(roles, role, await roles.GetClaimsAsync(role), permissions);
    }

    private static async Task SetAsync(RoleManager<IdentityRole<Guid>> roles, IdentityRole<Guid> role, IList<Claim> claims, Permission permissions)
    {
        var have = Of(claims);
        foreach (var permission in Permissions.Each)
        {
            var wanted = (permissions & permission) == permission;
            var held = (have & permission) == permission;
            if (wanted && !held)
            {
                await roles.AddClaimAsync(role, new Claim(Permissions.ClaimType, permission.ToString()));
            }
            else if (!wanted && held)
            {
                foreach (var claim in claims.Where(c => c.Type == Permissions.ClaimType && c.Value == permission.ToString()))
                {
                    await roles.RemoveClaimAsync(role, claim);
                }
            }
        }
    }
}

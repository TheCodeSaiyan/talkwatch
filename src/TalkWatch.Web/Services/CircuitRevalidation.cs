using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Checks an open page's sign-in every minute. A request reads permissions afresh (LivePermissions), but an interactive
/// page keeps the person it was opened as for as long as it stays open: without this, someone locked, demoted or signed
/// out elsewhere kept watching Now or editing a flow with what they had before. When anything has changed, the page
/// loses its sign-in and shows the sign-in prompt; a reload brings it back with what they hold now.
/// </summary>
public sealed class CircuitRevalidation(ILoggerFactory loggers, IServiceScopeFactory scopes, IOptions<IdentityOptions> identity)
    : RevalidatingServerAuthenticationStateProvider(loggers)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        StillHoldsAsync(authenticationState.User);

    /// <summary>Whether this person, as the page has them, is still who they are now: not locked, and holding the same roles and permissions.</summary>
    public async Task<bool> StillHoldsAsync(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.GetUserAsync(principal) is not { } user || await users.IsLockedOutAsync(user))
        {
            return false;
        }

        // A new stamp: their password changed, two-factor reset, or they were locked and unlocked.
        if (users.SupportsUserSecurityStamp && principal.FindFirstValue(identity.Value.ClaimsIdentity.SecurityStampClaimType) != await users.GetSecurityStampAsync(user))
        {
            return false;
        }

        // A role changed by hand doesn't change the stamp, on purpose, so it is compared here.
        var roles = await users.GetRolesAsync(user);
        if (!roles.ToHashSet(StringComparer.Ordinal).SetEquals(principal.FindAll(identity.Value.ClaimsIdentity.RoleClaimType).Select(c => c.Value)))
        {
            return false;
        }

        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var now = Permission.None;
        foreach (var name in roles)
        {
            if (await roleManager.FindByNameAsync(name) is { } role)
            {
                now |= await RolePermissions.GetAsync(roleManager, role);
            }
        }

        return now == Permissions.Of(principal);
    }
}

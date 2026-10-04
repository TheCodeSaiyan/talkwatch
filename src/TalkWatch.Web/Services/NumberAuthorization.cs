using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// A permission held site-wide, from the cookie, or on any number, from the person's roles there, read live: what lets
/// someone into a page whose work is about their own numbers, such as their own alerts.
/// </summary>
public sealed record SiteOrNumberRequirement(Permission SiteWide, Permission OnANumber) : IAuthorizationRequirement;

public sealed class SiteOrNumberHandler(IServiceScopeFactory scopes) : AuthorizationHandler<SiteOrNumberRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SiteOrNumberRequirement requirement)
    {
        if (Permissions.Each.Any(p => (requirement.SiteWide & p) == p && context.User.Can(p)))
        {
            context.Succeed(requirement);
            return;
        }

        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var me))
        {
            return;
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UsePrincipal(context.User);
        if (await NumberAccess.AnywhereAsync(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(), me, requirement.OnANumber))
        {
            context.Succeed(requirement);
        }
    }
}

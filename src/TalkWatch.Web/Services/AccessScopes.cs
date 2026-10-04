using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>The site this instance serves, known once start-up has made sure it exists.</summary>
public sealed class CurrentSite
{
    public Guid Id { get; set; }

    public string Region { get; set; } = "GB";
}

/// <summary>
/// Decides the <see cref="AccessScope"/> for one DI scope: the signed-in person for a request, or the whole site for
/// TalkWatch's own background work, which says so explicitly. Anything else gets <see cref="AccessScope.Nobody"/>.
/// </summary>
public sealed class AccessScopeHolder(IHttpContextAccessor http, CurrentSite site) : IAccessScopeSource
{
    public const string SiteClaim = "talkwatch:site";

    /// <summary>The number chosen in the rail's switcher, when there is one; see <see cref="AppUser.ContextDid"/>.</summary>
    public const string DidClaim = "talkwatch:did";

    private AccessScope? _system;
    private AccessScope? _signedIn;

    /// <summary>
    /// Asked by the database context each time a query runs. A signed-in person's scope is remembered for the rest of
    /// the request; Nobody never is, because the context can be created before authentication has finished.
    /// </summary>
    public AccessScope Current => _system ?? _signedIn ?? Remember(Resolve());

    private ClaimsPrincipal? _principal;

    /// <summary>For background services only: this DI scope works on the whole site.</summary>
    public void UseSystemScope() => _system = AccessScope.System(site.Id);

    /// <summary>
    /// Works as someone else, whatever request this runs in: a report built for its recipient when an admin presses
    /// Run now, say. Wins over the request's own user.
    /// </summary>
    public void UseScope(AccessScope scope) => _system = scope;

    /// <summary>
    /// For interactive components: after the first render a Blazor circuit has no HTTP request, so the signed-in person
    /// comes from the circuit's authentication state instead. Without this, a live page would see nothing.
    /// </summary>
    public void UsePrincipal(ClaimsPrincipal principal) => _principal = principal;

    private AccessScope Remember(AccessScope scope)
    {
        if (scope.UserId is not null)
        {
            _signedIn = scope;
        }

        return scope;
    }

    public AccessScope Resolve()
    {
        if (_system is not null)
        {
            return _system;
        }

        var user = http.HttpContext?.User is { Identity.IsAuthenticated: true } fromRequest ? fromRequest : _principal;
        if (user?.Identity?.IsAuthenticated != true
            || !Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)
            || !Guid.TryParse(user.FindFirstValue(SiteClaim), out var siteId)
            || siteId != site.Id)
        {
            return AccessScope.Nobody;
        }

        return AccessScope.ForUser(siteId, userId, Permissions.Of(user)) with { Did = user.FindFirstValue(DidClaim) };
    }
}

/// <summary>
/// Puts the user's site, and the number they have chosen if any, in their sign-in cookie, so the scope can be built
/// without a database read per request.
/// </summary>
public sealed class SiteClaimsFactory(UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles, IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<AppUser, IdentityRole<Guid>>(users, roles, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(AppUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AccessScopeHolder.SiteClaim, user.SiteId.ToString()));
        if (!string.IsNullOrEmpty(user.ContextDid))
        {
            identity.AddClaim(new Claim(AccessScopeHolder.DidClaim, user.ContextDid));
        }

        return identity;
    }
}

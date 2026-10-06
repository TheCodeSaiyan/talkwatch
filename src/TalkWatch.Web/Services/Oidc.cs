using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Sign-in through an OpenID Connect provider such as Authentik, alongside passwords. Set Oidc__Authority,
/// Oidc__ClientId and Oidc__ClientSecret (a secret file in production) to turn it on. Groups decide access: the
/// group mappings page gives groups roles and lines, Oidc__AdminGroups make someone an Admin whatever is mapped, and
/// Oidc__ViewerGroups let someone in without setting a role (comma-separated names). Someone whose groups match
/// nothing is turned away. Behind a reverse proxy, Proxy__TrustedNetworks must include the proxy, or the
/// redirect back comes to http:// and the provider refuses it.
/// </summary>
public sealed class OidcOptions
{
    public const string Section = "Oidc";
    public const string Scheme = "oidc";

    /// <summary>The provider's issuer, such as https://auth.example/application/o/talkwatch/. Sign-in through it is off while unset.</summary>
    public string? Authority { get; set; }

    /// <summary>The client id registered with the provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret registered with the provider.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>What the sign-in button says: 'Sign in with ...'.</summary>
    public string DisplayName { get; set; } = "Authentik";

    /// <summary>Groups whose members are Admins, comma-separated. Admin follows these at every sign-in.</summary>
    public string AdminGroups { get; set; } = "";

    /// <summary>
    /// Groups whose members may sign in without a mapping setting their role: a new account comes in as a Viewer.
    /// Comma-separated. Anyone their groups match nothing for, here or on the group mappings page, is turned away.
    /// </summary>
    public string ViewerGroups { get; set; } = "";

    /// <summary>The claim holding group names.</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>The claim holding the username, matched to an existing account's on first sign-in.</summary>
    public string UsernameClaim { get; set; } = "preferred_username";

    /// <summary>The claim with the person's email: kept on their account at each sign-in, for reports and email alerts.</summary>
    public string EmailClaim { get; set; } = "email";

    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) && !string.IsNullOrWhiteSpace(ClientId);

    public static IReadOnlySet<string> Names(string list) =>
        list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
}

public static partial class Oidc
{
    // Authentik's 'profile' scope carries the groups claim.
    private static readonly string[] Scopes = ["openid", "profile", "email"];

    public static void AddOidc(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(OidcOptions.Section).Get<OidcOptions>() ?? new OidcOptions();
        builder.Services.Configure<OidcOptions>(builder.Configuration.GetSection(OidcOptions.Section));
        if (!options.Enabled)
        {
            return;
        }

        builder.Services.AddAuthentication().AddOpenIdConnect(OidcOptions.Scheme, options.DisplayName, o =>
        {
            o.Authority = options.Authority;
            o.ClientId = options.ClientId;
            o.ClientSecret = options.ClientSecret;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            o.Scope.Clear();
            foreach (var scope in Scopes)
            {
                o.Scope.Add(scope);
            }

            // Claims as the provider names them ('groups', 'preferred_username'), not remapped to long URIs.
            o.MapInboundClaims = false;
            o.GetClaimsFromUserInfoEndpoint = true;

            // The handler copies only a few user-info fields by default; these two are what TalkWatch decides on.
            o.ClaimActions.MapUniqueJsonKey(options.UsernameClaim, options.UsernameClaim);
            o.ClaimActions.MapJsonKey(options.GroupsClaim, options.GroupsClaim);
            o.ClaimActions.MapUniqueJsonKey(options.EmailClaim, options.EmailClaim);
            o.SaveTokens = false;
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.CallbackPath = "/signin-oidc";
        });
    }

    public static void MapOidc(this IEndpointRouteBuilder app)
    {
        // Starts the round trip to the provider.
        app.MapGet("/account/signin/oidc", (string? returnUrl, SignInManager<AppUser> signIn, IOptions<OidcOptions> options) =>
        {
            if (!options.Value.Enabled)
            {
                return Results.NotFound();
            }

            var back = $"/account/signin/oidc/done?returnUrl={Uri.EscapeDataString(AccountEndpoints.LocalOr(returnUrl))}";
            return Results.Challenge(signIn.ConfigureExternalAuthenticationProperties(OidcOptions.Scheme, back), [OidcOptions.Scheme]);
        });

        // Back from the provider, who has proved who this is: find or make their account and sign them in.
        app.MapGet("/account/signin/oidc/done", async (string? returnUrl, HttpContext http, SignInManager<AppUser> signIn,
            IServiceScopeFactory scopes, CurrentSite site, IOptions<OidcOptions> options, LineDirectorySync directory, TimeProvider clock, ILogger<OidcOptions> logger) =>
        {
            var target = AccountEndpoints.LocalOr(returnUrl);
            var info = await signIn.GetExternalLoginInfoAsync();
            if (info is null)
            {
                return Results.Redirect("/signin?oidc=failed");
            }

            await http.SignOutAsync(IdentityConstants.ExternalScheme);

            // Nobody is signed in yet, so the account, its role and its grants by groups are seen to as TalkWatch.
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var outcome = await AccountForAsync(info, scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>(),
                scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>(), scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(),
                site, options.Value, directory.Current, clock.GetUtcNow());
            if (outcome.User is not { } user)
            {
                LogRefused(logger, info.Principal.FindFirstValue(options.Value.UsernameClaim) ?? info.ProviderKey, outcome.Refusal!);
                return Results.Redirect($"/signin?oidc={outcome.Refusal}");
            }

            // The provider did the checking, second factor included; TalkWatch's own code is for its passwords.
            await signIn.SignInAsync(user, isPersistent: false, authenticationMethod: OidcOptions.Scheme);
            return Results.LocalRedirect(target);
        });
    }

    private sealed record Outcome(AppUser? User, string? Refusal);

    /// <summary>
    /// Links the provider to the signed-in person's own account: how an account that signs in with a password comes to
    /// sign in through the provider too. A form post with its antiforgery token, so another site cannot start it.
    /// </summary>
    public static void MapOidcLink(this IEndpointRouteBuilder app)
    {
        app.MapPost("/account/link/oidc", async (HttpContext http, SignInManager<AppUser> signIn, UserManager<AppUser> users, IOptions<OidcOptions> options, IAntiforgery antiforgery) =>
        {
            if (!options.Value.Enabled)
            {
                return Results.NotFound();
            }

            // Checked here: a minimal API checks the token only when it binds a form, and this one binds none.
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                return Results.BadRequest();
            }

            // The account's id goes with the round trip and must come back with it, so the link is to this account only.
            return Results.Challenge(
                signIn.ConfigureExternalAuthenticationProperties(OidcOptions.Scheme, "/account/link/oidc/done", users.GetUserId(http.User)), [OidcOptions.Scheme]);
        }).RequireAuthorization();

        app.MapGet("/account/link/oidc/done", async (HttpContext http, SignInManager<AppUser> signIn, UserManager<AppUser> users, Audit audit) =>
        {
            if (await users.GetUserAsync(http.User) is not { } user)
            {
                return Results.Redirect("/signin");
            }

            var info = await signIn.GetExternalLoginInfoAsync(user.Id.ToString());
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            if (info is null)
            {
                return Results.Redirect("/account?oidc=failed");
            }

            if (await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey) is { } linked)
            {
                return Results.Redirect(linked.Id == user.Id ? "/account?oidc=linked" : "/account?oidc=taken");
            }

            if (!(await users.AddLoginAsync(user, new UserLoginInfo(info.LoginProvider, info.ProviderKey, info.ProviderDisplayName))).Succeeded)
            {
                return Results.Redirect("/account?oidc=failed");
            }

            await audit.WriteAsync("oidc.link", "user", user.Id, info.ProviderDisplayName);
            return Results.Redirect("/account?oidc=linked");
        }).RequireAuthorization();
    }

    /// <summary>
    /// What the provider says of the person, kept on their account at every sign-in: their email, so reports and email
    /// alerts reach them and follow a change made in the provider. And, when nobody has linked them to a Talk user yet,
    /// the Talk user with that same email, so a flow's "whoever it rang" and "whoever is free" reach them too; a Talk user
    /// already linked to someone else is left as it is.
    /// </summary>
    private static async Task SyncProfileAsync(UserManager<AppUser> users, TalkWatchDbContext db, AppUser user, string? email, Core.Talk.LineDirectory directory)
    {
        email = email?.Trim();
        var changed = false;
        if (!string.IsNullOrEmpty(email) && email.Contains('@', StringComparison.Ordinal) && !string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase))
        {
            (user.Email, user.NormalizedEmail, user.EmailConfirmed) = (email, users.NormalizeEmail(email), true);
            changed = true;
        }

        if (user.TalkUserUuid is null && !string.IsNullOrEmpty(email)
            && directory.Users.FirstOrDefault(u => !u.HideFromUserList && string.Equals(u.Email?.Trim(), email, StringComparison.OrdinalIgnoreCase)) is { } talk
            && !await db.Users.AnyAsync(u => u.TalkUserUuid == talk.Uuid))
        {
            user.TalkUserUuid = talk.Uuid;
            changed = true;
        }

        if (changed)
        {
            await users.UpdateAsync(user);
        }
    }

    /// <summary>
    /// The account for someone the provider signed in, made or linked as needed, with its role and its grants by groups
    /// kept in step with their groups (<see cref="GroupAccess"/>): leaving a group in the provider takes away what it
    /// gave at their next sign-in. Grants given by hand are left alone.
    /// </summary>
    private static async Task<Outcome> AccountForAsync(ExternalLoginInfo info, UserManager<AppUser> users, RoleManager<IdentityRole<Guid>> roles,
        TalkWatchDbContext db, CurrentSite site, OidcOptions options, Core.Talk.LineDirectory directory, DateTimeOffset now)
    {
        var groups = info.Principal.FindAll(options.GroupsClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        var mappings = await db.GroupMappings.AsNoTracking().Include(m => m.Lines).ToListAsync();
        var access = GroupAccess.Of(groups, mappings, OidcOptions.Names(options.AdminGroups), OidcOptions.Names(options.ViewerGroups));
        if (!access.Allowed)
        {
            return new Outcome(null, "not-allowed");
        }

        // A role a mapping names that has since been removed sets nothing.
        var role = access.Role is { } named && await roles.RoleExistsAsync(named) ? named : null;

        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);
        if (user is null)
        {
            var username = info.Principal.FindFirstValue(options.UsernameClaim);
            if (string.IsNullOrWhiteSpace(username))
            {
                return new Outcome(null, "no-username");
            }

            // An account of the same name is not theirs for the asking: some providers let people choose their own
            // username, and linking by it would hand them an account, an admin's say, and skip its password and second
            // factor. One with a password is linked only by its owner, signed in with it, from their account page.
            user = await users.FindByNameAsync(username);
            if (user is not null && (user.SiteId != site.Id || await users.HasPasswordAsync(user)))
            {
                return new Outcome(null, user.SiteId != site.Id ? "not-allowed" : "link-first");
            }

            if (user is null)
            {
                user = new AppUser { Id = Guid.NewGuid(), UserName = username, SiteId = site.Id };
                if (!(await users.CreateAsync(user)).Succeeded)
                {
                    return new Outcome(null, "failed");
                }

                await users.AddToRoleAsync(user, role ?? Roles.Viewer);
            }

            await users.AddLoginAsync(user, new UserLoginInfo(info.LoginProvider, info.ProviderKey, info.ProviderDisplayName));
        }

        if (await users.IsLockedOutAsync(user))
        {
            return new Outcome(null, "locked");
        }

        // The role their groups set; with none set, an Admin who is no longer in an admin group becomes a Viewer, and
        // anyone else keeps the role given them by hand.
        var current = await users.GetRolesAsync(user);
        var wanted = role ?? (current.Contains(Roles.Admin) ? Roles.Viewer : null);
        if (wanted is not null && !(current.Count == 1 && current[0] == wanted))
        {
            await users.RemoveFromRolesAsync(user, current);
            await users.AddToRoleAsync(user, wanted);
        }

        await SyncProfileAsync(users, db, user, info.Principal.FindFirstValue(options.EmailClaim), directory);
        await SyncGrantsAsync(db, site, user, access.Lines, now);
        return new Outcome(user, null);
    }

    /// <summary>Makes someone's grants by groups exactly the lines their groups give now.</summary>
    private static async Task SyncGrantsAsync(TalkWatchDbContext db, CurrentSite site, AppUser user, IReadOnlyList<GroupLine> lines, DateTimeOffset now)
    {
        var existing = await db.Grants.Where(g => g.UserId == user.Id && g.ByGroups).ToListAsync();
        foreach (var line in lines)
        {
            var grant = existing.FirstOrDefault(g => g.Kind == line.Kind && g.Key == line.Key);
            if (grant is null)
            {
                db.Grants.Add(new Grant
                {
                    Id = Guid.NewGuid(), SiteId = site.Id, UserId = user.Id, Kind = line.Kind, Key = line.Key, ByGroups = true, Groups = line.Groups,
                    AllowRecordings = line.AllowRecordings, AllowVoicemail = line.AllowVoicemail, AllowTranscripts = line.AllowTranscripts, CreatedAt = now,
                });
            }
            else
            {
                existing.Remove(grant);
                (grant.AllowRecordings, grant.AllowVoicemail, grant.AllowTranscripts, grant.Groups) = (line.AllowRecordings, line.AllowVoicemail, line.AllowTranscripts, line.Groups);
            }
        }

        db.Grants.RemoveRange(existing);
        await db.SaveChangesAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sign-in through the identity provider refused for {Who}: {Reason}.")]
    private static partial void LogRefused(ILogger logger, string who, string reason);
}

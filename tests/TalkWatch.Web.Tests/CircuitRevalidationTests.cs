using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>An open page loses its sign-in once the person it was opened as is locked, given another role, or their role changes.</summary>
public sealed class CircuitRevalidationTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static readonly string[] People = ["pat", "sam", "kim"];

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider);
    }

    /// <summary>The person as a page opened now would have them.</summary>
    private static Task<ClaimsPrincipal> SignedInAsAsync(WebApplicationFactory<Program> app, string username) => AsSystemAsync(app, async s =>
        await s.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>().CreateAsync((await s.GetRequiredService<UserManager<AppUser>>().FindByNameAsync(username))!));

    private static Task<bool> StillHoldsAsync(WebApplicationFactory<Program> app, ClaimsPrincipal principal) => AsSystemAsync(app, s =>
        ((CircuitRevalidation)s.GetRequiredService<AuthenticationStateProvider>()).StillHoldsAsync(principal));

    [Fact]
    public async Task A_page_keeps_its_sign_in_until_the_person_is_given_another_role_their_role_changes_or_they_are_locked()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await AsSystemAsync(app, async s =>
        {
            var users = s.GetRequiredService<UserManager<AppUser>>();
            var site = (await users.FindByNameAsync(TalkWatchApp.AdminUsername))!.SiteId;
            foreach (var name in People)
            {
                var user = new AppUser { Id = Guid.NewGuid(), UserName = name, SiteId = site };
                await users.CreateAsync(user, "a long enough password");
                await users.AddToRoleAsync(user, Roles.Viewer);
            }

            return 0;
        });
        var pat = await SignedInAsAsync(app, "pat");
        var sam = await SignedInAsAsync(app, "sam");
        var kim = await SignedInAsAsync(app, "kim");
        Assert.True(await StillHoldsAsync(app, pat));

        await AsSystemAsync(app, async s =>
        {
            var users = s.GetRequiredService<UserManager<AppUser>>();
            var who = (await users.FindByNameAsync("pat"))!;
            await users.RemoveFromRoleAsync(who, Roles.Viewer);
            return await users.AddToRoleAsync(who, Roles.Admin);
        });
        Assert.False(await StillHoldsAsync(app, pat));

        await AsSystemAsync(app, async s => await users(s).SetLockoutEndDateAsync((await users(s).FindByNameAsync("kim"))!, DateTimeOffset.MaxValue));
        Assert.False(await StillHoldsAsync(app, kim));
        Assert.True(await StillHoldsAsync(app, sam));

        await AsSystemAsync(app, async s =>
        {
            var roles = s.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
            await RolePermissions.ChangeAsync(roles, (await roles.FindByNameAsync(Roles.Viewer))!, Permission.AllCalls);
            return 0;
        });
        Assert.False(await StillHoldsAsync(app, sam));

        static UserManager<AppUser> users(IServiceProvider s) => s.GetRequiredService<UserManager<AppUser>>();
    }
}

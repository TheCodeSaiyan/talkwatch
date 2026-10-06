using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// A form post that takes nothing but what is in its address (lock, delete, sweep, sign out) still needs the page's
/// antiforgery token: .NET checks it by itself only where a form is read, and a site on a sibling subdomain counts as
/// the same site to a SameSite=Strict cookie.
/// </summary>
public sealed class FormTokenTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<UserManager<AppUser>, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>());
    }

    [Fact]
    public async Task A_post_without_the_pages_token_is_refused_and_changes_nothing()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        var victim = await AsSystemAsync(app, async users =>
        {
            var user = new AppUser { Id = Guid.NewGuid(), UserName = "victim", SiteId = (await users.FindByNameAsync(TalkWatchApp.AdminUsername))!.SiteId };
            await users.CreateAsync(user, "a long enough password");
            await users.AddToRoleAsync(user, Roles.Viewer);
            return user.Id;
        });

        async Task<HttpStatusCode> ForgeAsync(string action)
        {
            using var empty = new FormUrlEncodedContent([]);
            return (await admin.PostAsync(new Uri(action, UriKind.Relative), empty, Ct)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.BadRequest, await ForgeAsync($"/admin/users/{victim}/lock"));
        Assert.Equal(HttpStatusCode.BadRequest, await ForgeAsync("/admin/retention/sweep"));
        Assert.Equal(HttpStatusCode.BadRequest, await ForgeAsync("/account/browser-alerts"));
        Assert.Equal(HttpStatusCode.BadRequest, await ForgeAsync("/account/signout"));

        Assert.False(await AsSystemAsync(app, async users => await users.IsLockedOutAsync((await users.FindByIdAsync(victim.ToString()))!)));
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(new Uri("/calls", UriKind.Relative), Ct)).StatusCode);
    }
}

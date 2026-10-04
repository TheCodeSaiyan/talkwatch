using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

public static class AccountEndpoints
{
    public sealed record SignInForm(string? Username, string? Password, string? ReturnUrl);

    public sealed record NumberForm(string? Did, string? ReturnUrl);

    public sealed class CodeForm
    {
        public string? Code { get; set; }
        public bool Recovery { get; set; }
        public string? ReturnUrl { get; set; }
    }

    public static void MapAccount(this IEndpointRouteBuilder app)
    {
        // Form posts, protected by the antiforgery token the sign-in page renders. A failed attempt counts towards
        // lockout, and the page says only that sign-in failed, never which half was wrong.
        app.MapPost("/account/signin", async ([FromForm] SignInForm form, SignInManager<AppUser> signIn) =>
        {
            var returnUrl = IsLocal(form.ReturnUrl) ? form.ReturnUrl! : Home;
            if (string.IsNullOrEmpty(form.Username) || string.IsNullOrEmpty(form.Password))
            {
                return Results.Redirect($"/signin?failed=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            var result = await signIn.PasswordSignInAsync(form.Username, form.Password, isPersistent: false, lockoutOnFailure: true);
            if (result.RequiresTwoFactor)
            {
                // The password was right; the code page finishes signing in.
                return Results.Redirect($"/signin/code?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            return result.Succeeded
                ? Results.LocalRedirect(returnUrl)
                : Results.Redirect($"/signin?failed=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }).RequireRateLimiting(Protection.SignInLimit);

        // The second step, for someone whose password was right moments ago: Identity keeps who in its own short-lived
        // cookie. Wrong codes count towards the same lockout as wrong passwords.
        app.MapPost("/account/signin/code", async ([FromForm] CodeForm form, SignInManager<AppUser> signIn) =>
        {
            var returnUrl = IsLocal(form.ReturnUrl) ? form.ReturnUrl! : Home;
            if (await signIn.GetTwoFactorAuthenticationUserAsync() is null)
            {
                return Results.Redirect($"/signin?returnUrl={Uri.EscapeDataString(returnUrl)}");
            }

            // Spaces are forgiven in both. A recovery code keeps its dash, which is part of it; an app's code has none.
            var code = (form.Code ?? "").Replace(" ", "", StringComparison.Ordinal);
            var result = form.Recovery
                ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
                : await signIn.TwoFactorAuthenticatorSignInAsync(code.Replace("-", "", StringComparison.Ordinal), isPersistent: false, rememberClient: false);
            return result.Succeeded
                ? Results.LocalRedirect(returnUrl)
                : result.IsLockedOut
                    ? Results.Redirect($"/signin?failed=true&returnUrl={Uri.EscapeDataString(returnUrl)}")
                    : Results.Redirect($"/signin/code?failed=true&returnUrl={Uri.EscapeDataString(returnUrl)}");
        }).RequireRateLimiting(Protection.SignInLimit);

        // The rail's number switcher: one DID this person can see, or empty for all numbers. Kept on the account, then put
        // in a fresh sign-in cookie so the next page is narrowed by it; back to the page they were on.
        app.MapPost("/account/number", async ([FromForm] NumberForm form, HttpContext http, UserManager<AppUser> users, SignInManager<AppUser> signIn, TalkWatchDbContext db) =>
        {
            var user = await users.GetUserAsync(http.User);
            if (user is null)
            {
                return Results.Redirect("/signin");
            }

            var did = string.IsNullOrWhiteSpace(form.Did) ? null : form.Did.Trim();
            if (did is not null && !await db.Lines.AnyAsync(l => l.Kind == LineKind.Did && l.Key == did))
            {
                return Results.BadRequest();
            }

            user.ContextDid = did;
            await users.UpdateAsync(user);
            await signIn.RefreshSignInAsync(user);
            return Results.LocalRedirect(LocalOr(form.ReturnUrl));
        }).RequireAuthorization();

        app.MapPost("/account/signout", async (SignInManager<AppUser> signIn) =>
        {
            await signIn.SignOutAsync();
            return Results.Redirect("/signin");
        }).RequireAuthorization();
    }

    /// <summary>The URL if it is a path on this site, else the calls page.</summary>
    /// <summary>Where TalkWatch opens: Now, the live board, first of the rail's workspaces.</summary>
    public const string Home = "/live";

    public static string LocalOr(string? url) => IsLocal(url) ? url! : Home;

    // Only paths on this site: '/calls' yes, '//evil.example' and 'https://evil.example' no.
    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}

using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;

namespace TalkWatch.Web.Tests;

public sealed partial class SignInTests
{
    /// <summary>Identity's own hasher, counting how often a password is checked.</summary>
    private sealed class CountingHasher : IPasswordHasher<AppUser>
    {
        private readonly PasswordHasher<AppUser> _inner = new();

        public int Checks { get; private set; }

        public string HashPassword(AppUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(AppUser user, string hashedPassword, string providedPassword)
        {
            Checks++;
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    // Checking a password is slow on purpose, so answering at once for a name with no account told anyone timing the
    // answer which names have one. An unknown name now costs the same check.
    [Fact]
    public async Task A_username_with_no_account_takes_a_password_check_like_one_with_an_account()
    {
        var hasher = new CountingHasher();
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), services: s => s.AddSingleton<IPasswordHasher<AppUser>>(hasher));
        using var browser = TalkWatchApp.Browser(app);

        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, "not the admin's password");
        var known = hasher.Checks;
        await TalkWatchApp.SignInAsync(browser, "nobody-of-that-name", "not a real password");

        Assert.Equal(1, known);
        Assert.Equal(2, hasher.Checks);
    }
}

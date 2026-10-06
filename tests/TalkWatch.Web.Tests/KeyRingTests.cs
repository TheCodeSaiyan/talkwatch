using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>
/// Credentials at rest: everything TalkWatch encrypts is encrypted with keys kept in the database, so those keys are
/// themselves encrypted with one that is not, and a copy of the database alone reads none of them.
/// </summary>
public sealed class KeyRingTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<T> DbAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    private static string KeyFile(WebApplicationFactory<Program> app) =>
        Path.Combine(app.Services.GetRequiredService<IOptions<AudioOptions>>().Value.Path, ".keys", "data-protection.key");

    [Fact]
    public async Task The_keys_in_the_database_are_stored_encrypted_with_a_key_kept_on_the_volume()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var secret = app.Services.GetRequiredService<ChannelSecrets>().Protect("the console's password");

        var keys = await DbAsync(app, db => db.DataProtectionKeys.Select(k => k.Xml!).ToListAsync(Ct));

        Assert.NotEmpty(keys);
        Assert.All(keys, xml =>
        {
            var key = XElement.Parse(xml);
            Assert.DoesNotContain(key.Descendants(), e => (bool?)e.Attribute(DataProtection + "requiresEncryption") == true);
            Assert.Contains(key.Descendants(), e => e.Name == DataProtection + "encryptedSecret");
        });
        Assert.True(File.Exists(KeyFile(app)));
        Assert.Equal("the console's password", app.Services.GetRequiredService<ChannelSecrets>().Unprotect(secret));
    }

    [Fact]
    public async Task Keys_and_two_factor_keys_stored_before_are_encrypted_at_the_next_start_and_still_work()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var secret = app.Services.GetRequiredService<ChannelSecrets>().Protect("the console's password");
        using var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        // The account page makes an authenticator key, to show for setting up two-factor sign-in.
        await admin.GetStringAsync(new Uri("/account", UriKind.Relative), Ct);
        var shown = await DbAsync(app, async db =>
        {
            var token = await db.UserTokens.SingleAsync(Ct);
            Assert.StartsWith("dp1:", token.Value, StringComparison.Ordinal);
            return app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("TalkWatch.UserTokens.v1").Unprotect(token.Value![4..]);
        });

        // As an older TalkWatch left them: each key's secret as it is, and the two-factor key unencrypted.
        var decryptor = new KeyRingDecryptor(app.Services);
        await DbAsync(app, async db =>
        {
            foreach (var row in await db.DataProtectionKeys.ToListAsync(Ct))
            {
                var xml = XElement.Parse(row.Xml!);
                foreach (var encrypted in xml.Descendants(DataProtection + "encryptedSecret").ToList())
                {
                    encrypted.ReplaceWith(decryptor.Decrypt(encrypted.Elements().Single()));
                }

                row.Xml = xml.ToString();
            }

            (await db.UserTokens.SingleAsync(Ct)).Value = shown;
            return await db.SaveChangesAsync(Ct);
        });
        Assert.Contains("requiresEncryption", await DbAsync(app, db => db.DataProtectionKeys.Select(k => k.Xml!).FirstAsync(Ct)), StringComparison.Ordinal);

        await DbAsync(app, async db =>
        {
            await KeyRing.EncryptStoredAsync(app.Services, db, Ct);
            return 0;
        });

        var keys = await DbAsync(app, db => db.DataProtectionKeys.Select(k => k.Xml!).ToListAsync(Ct));
        Assert.All(keys, xml => Assert.DoesNotContain("requiresEncryption", xml, StringComparison.Ordinal));
        Assert.StartsWith("dp1:", await DbAsync(app, db => db.UserTokens.Select(t => t.Value).SingleAsync(Ct)), StringComparison.Ordinal);
        // Read back as data protection does after a restart, from the stored XML rather than what it holds in memory: the
        // decryptor found by the type name each key names, and the key's secret back as it was.
        Assert.All(keys, xml => Assert.All(XElement.Parse(xml).Descendants(DataProtection + "encryptedSecret"), encrypted =>
        {
            var type = Type.GetType((string)encrypted.Attribute("decryptorType")!, throwOnError: true)!;
            var decrypted = ((Microsoft.AspNetCore.DataProtection.XmlEncryption.IXmlDecryptor)ActivatorUtilities.CreateInstance(app.Services, type)).Decrypt(encrypted.Elements().Single());
            Assert.Equal("masterKey", decrypted.Name.LocalName);
        }));
        Assert.Equal("the console's password", app.Services.GetRequiredService<ChannelSecrets>().Unprotect(secret));
        // The account page still shows the same setup key, read through the store now that it is encrypted.
        Assert.Contains(shown.ToLowerInvariant()[..4], await admin.GetStringAsync(new Uri("/account", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_given_as_a_secret_is_used_and_no_key_file_is_made()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory),
            settings: new Dictionary<string, string> { ["DataProtection:Key"] = "a key given as a docker secret, long enough" });
        app.Services.GetRequiredService<ChannelSecrets>().Protect("anything");

        Assert.Equal("DataProtection__Key", app.Services.GetRequiredService<KeyEncryptionKey>().Source);
        Assert.False(File.Exists(KeyFile(app)));
    }
}

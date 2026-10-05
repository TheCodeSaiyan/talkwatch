using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The Console page: the console, the account and the way to it, set by an admin while TalkWatch runs.</summary>
public sealed partial class ConsolePageTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Keys made up for the tests: 32 bytes each, in base64 as WireGuard writes them.
    private static readonly string ClientKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string GatewayKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    private static string GatewayFile(string allowed = "192.168.1.0/24") => $"""
        [Interface]
        PrivateKey = {ClientKey}
        Address = 192.168.3.2/32

        [Peer]
        PublicKey = {GatewayKey}
        AllowedIPs = {allowed}
        Endpoint = talk-site.example.net:51820
        """;

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    private static async Task<string> TokenAsync(HttpClient browser) =>
        WebUtility.HtmlDecode(Token().Match(await browser.GetStringAsync(new Uri("/admin/console", UriKind.Relative), Ct)).Groups[1].Value);

    private static async Task<string> PostAsync(HttpClient browser, string action, params (string Name, string Value)[] fields)
    {
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)).Append(new("__RequestVerificationToken", await TokenAsync(browser)));
        using var content = new FormUrlEncodedContent(form);
        var response = await browser.PostAsync(new Uri(action, UriKind.Relative), content, Ct);
        return Uri.UnescapeDataString(response.Headers.Location?.OriginalString ?? response.StatusCode.ToString());
    }

    private static async Task<T> AsSystemAsync<T>(WebApplicationFactory<Program> app, Func<TalkWatchDbContext, Task<T>> work)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
    }

    private async Task<(WebApplicationFactory<Program> App, HttpClient Admin)> StartAsync(IReadOnlyDictionary<string, string>? settings = null)
    {
        var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory), settings: settings);
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return (app, admin);
    }

    [Fact]
    public async Task Only_an_admin_sees_the_page_or_changes_anything()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;
        using (var scope = app.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "viewer", SiteId = scope.ServiceProvider.GetRequiredService<CurrentSite>().Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
        }

        using var viewerBrowser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(viewerBrowser, "viewer", "a long enough password");
        var page = await viewerBrowser.GetAsync(new Uri("/admin/console", UriKind.Relative), Ct);
        var adminToken = await TokenAsync(admin);
        using var content = new FormUrlEncodedContent([new("ConsoleUrl", "https://203.0.113.9"), new("__RequestVerificationToken", adminToken)]);
        var post = await viewerBrowser.PostAsync(new Uri("/admin/console", UriKind.Relative), content, Ct);

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain("/admin/console?msg", post.Headers.Location?.OriginalString ?? "", StringComparison.Ordinal);
        Assert.False(await AsSystemAsync(app, db => db.ConsoleSettings.AnyAsync(Ct)));
        Assert.DoesNotContain("/admin/console", await viewerBrowser.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct), StringComparison.Ordinal);
        Assert.Contains("Console", await admin.GetStringAsync(new Uri("/admin/console", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    // The page wins field by field; a field left empty falls back to the Talk__ setting.
    [Fact]
    public async Task What_is_saved_on_the_page_wins_over_the_settings_field_by_field()
    {
        var (app, admin) = await StartAsync(new Dictionary<string, string> { ["Talk:ConsoleUrl"] = "https://192.168.1.1", ["Talk:CertificateSha256"] = new string('A', 64) });
        await using var _ = app;
        using var __ = admin;

        var saved = await PostAsync(admin, "/admin/console", ("Username", "talkwatch-page"), ("Password", "a password from the page"), ("ConsoleUrl", ""));
        var target = await app.Services.GetRequiredService<ConsoleConnection>().GetAsync(Ct);
        var row = await AsSystemAsync(app, db => db.ConsoleSettings.SingleAsync(Ct));

        Assert.Contains("Saved.", saved, StringComparison.Ordinal);
        Assert.Equal("talkwatch-page", target.Username);
        Assert.Equal("a password from the page", target.Password);
        Assert.Equal(new Uri("https://192.168.1.1"), target.Url);
        Assert.Equal(new string('A', 64), target.CertificateSha256);
        // Encrypted at rest, and named but never quoted in the audit log.
        Assert.DoesNotContain("a password from the page", row.ProtectedPassword!, StringComparison.Ordinal);
        Assert.Contains(await AsSystemAsync(app, db => db.AuditEvents.Where(e => e.Action == "console.settings").Select(e => e.Detail).ToListAsync(Ct)),
            d => d!.Contains("password saved", StringComparison.Ordinal) && !d.Contains("a password from the page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_gateways_WireGuard_file_is_saved_encrypted_and_the_page_never_shows_its_key()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        // Uploaded as the gateway gave it out, as a browser sends a file.
        using var form = new MultipartFormDataContent
        {
            { new StringContent(await TokenAsync(admin)), "__RequestVerificationToken" },
            { new StringContent("https://192.168.1.1"), "ConsoleUrl" },
            { new StringContent("WireGuard"), "Route" },
            { new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(GatewayFile())), "WireGuardFile", "talkwatch.conf" },
        };
        var response = await admin.PostAsync(new Uri("/admin/console", UriKind.Relative), form, Ct);
        var page = await admin.GetStringAsync(new Uri("/admin/console", UriKind.Relative), Ct);
        var target = await app.Services.GetRequiredService<ConsoleConnection>().GetAsync(Ct);
        var row = await AsSystemAsync(app, db => db.ConsoleSettings.SingleAsync(Ct));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ConsoleRoute.WireGuard, target.Route);
        Assert.Null(target.Problem);
        Assert.Equal("talk-site.example.net", target.WireGuard!.EndpointHost);
        Assert.Contains("talk-site.example.net:51820", page, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientKey, page, StringComparison.Ordinal);
        Assert.DoesNotContain(ClientKey, row.ProtectedWireGuardConfig!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WireGuard_typed_in_one_by_one_is_saved_the_same_way()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        await PostAsync(admin, "/admin/console", ("ConsoleUrl", "https://192.168.1.1"), ("Route", "WireGuard"),
            ("WireGuardPrivateKey", ClientKey), ("WireGuardAddress", "192.168.3.2/32"), ("WireGuardPeerPublicKey", GatewayKey),
            ("WireGuardEndpoint", "talk-site.example.net:51820"), ("WireGuardAllowedIps", "192.168.1.1/32"));
        var target = await app.Services.GetRequiredService<ConsoleConnection>().GetAsync(Ct);

        Assert.Null(target.Problem);
        Assert.Equal(["192.168.1.1/32"], target.WireGuard!.AllowedIps);
    }

    [Fact]
    public async Task A_WireGuard_file_that_will_not_work_is_refused_saying_why_and_nothing_is_saved()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var refused = await PostAsync(admin, "/admin/console", ("Route", "WireGuard"), ("WireGuardConf", "[Interface]\nPrivateKey = not a key\n"));

        Assert.Contains("weren't saved", refused, StringComparison.Ordinal);
        Assert.False(await AsSystemAsync(app, db => db.ConsoleSettings.AnyAsync(Ct)));
    }

    [Fact]
    public async Task A_console_outside_the_tunnels_allowed_addresses_is_saved_but_flagged()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        var saved = await PostAsync(admin, "/admin/console", ("ConsoleUrl", "https://10.0.0.1"), ("Route", "WireGuard"), ("WireGuardConf", GatewayFile("192.168.1.0/24")));

        Assert.Contains("isn't in the tunnel's allowed addresses", saved, StringComparison.Ordinal);
    }

    // In a build without the helpers, which is what the tests run, a tunnel can't start: everyone sees it's down.
    [Fact]
    public async Task Everyone_sees_in_the_rail_when_the_tunnel_is_down()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        await PostAsync(admin, "/admin/console", ("ConsoleUrl", "https://192.168.1.1"), ("Route", "WireGuard"), ("WireGuardConf", GatewayFile()));
        var tunnel = app.Services.GetRequiredService<ConsoleTunnel>();
        for (var i = 0; i < 100 && tunnel.Status.State != TunnelState.Down; i++)
        {
            await Task.Delay(50, Ct);
        }

        var page = await admin.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct);

        Assert.Equal(TunnelState.Down, tunnel.Status.State);
        Assert.Contains("data-tunnel=\"Down\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("talk-site.example.net", page, StringComparison.Ordinal);
    }

    // Started with no console at all, as a fresh install on a cloud host is: setting one on the page starts copying calls,
    // with no restart.
    [Fact]
    public async Task A_console_set_on_the_page_after_starting_is_copied_from_without_a_restart()
    {
        var (app, admin) = await StartAsync(new Dictionary<string, string> { ["Talk:PollSeconds"] = "10" });
        await using var _ = app;
        using var __ = admin;
        Assert.False(await AsSystemAsync(app, db => db.Calls.AnyAsync(Ct)));

        await PostAsync(admin, "/admin/console", ("ConsoleUrl", "https://192.168.1.1"), ("Route", "Direct"));
        for (var i = 0; i < 300 && !await AsSystemAsync(app, db => db.Calls.AnyAsync(Ct)); i++)
        {
            await Task.Delay(100, Ct);
        }

        Assert.True(await AsSystemAsync(app, db => db.Calls.AnyAsync(Ct)));
    }

    // Test connection signs in afresh with what is saved and reads the console's versions: the whole way there at once.
    [Fact]
    public async Task Test_connection_signs_in_with_what_is_saved_and_says_what_the_console_runs()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        await PostAsync(admin, "/admin/console", ("ConsoleUrl", "https://192.168.1.1"), ("Route", "Direct"));
        var tested = await PostAsync(admin, "/admin/console/test");

        Assert.Contains("Connected: Talk 5.3.2 on UniFi OS 5.1.33", tested, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_connection_says_when_the_console_refuses_the_account()
    {
        var (app, admin) = await StartAsync();
        await using var _ = app;
        using var __ = admin;

        await PostAsync(admin, "/admin/console", ("ConsoleUrl", "https://192.168.1.1"), ("Password", "not the console's password"));
        var tested = await PostAsync(admin, "/admin/console/test");

        Assert.Contains("refused", tested, StringComparison.Ordinal);
    }
}

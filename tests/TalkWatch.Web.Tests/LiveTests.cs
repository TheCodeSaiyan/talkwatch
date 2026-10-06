using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class LiveTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IReadOnlyList<LiveMessage> FixtureMessages() =>
        [.. Directory.GetFiles(FixtureConsole.DefaultDirectory, "*-ws-proxy-talk.json").Order().Select(f => LiveMessage.Parse(File.ReadAllText(f))!)];

    private static List<TalkUser> FixtureUsers() =>
        System.Text.Json.JsonSerializer.Deserialize<List<TalkUser>>(
            File.ReadAllBytes(Directory.GetFiles(FixtureConsole.DefaultDirectory, "*-http-proxy-talk-api-users.json").First()), TalkJson.Options)!;

    /// <summary>Plays the captured live session into the app, as the listener would receive it.</summary>
    private static async Task PlayCapturedSessionAsync(WebApplicationFactory<Program> app)
    {
        app.Services.GetRequiredService<LiveStatus>().SetDirectory(FixtureUsers());
        var listener = app.Services.GetRequiredService<LiveListener>();
        foreach (var message in FixtureMessages())
        {
            await listener.HandleAsync(message, Ct);
        }
    }

    [Fact]
    public async Task Live_call_updates_are_stored_and_presence_and_handsets_are_kept()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));

        await PlayCapturedSessionAsync(app);

        var uuids = FixtureMessages().Where(m => m.Event == LiveMessage.CallLogUpdated).SelectMany(m => m.CallRecords()).Select(r => r.Uuid).Distinct().ToList();
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var stored = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Calls.CountAsync(c => uuids.Contains(c.TalkUuid), Ct);
        var (users, devices) = app.Services.GetRequiredService<LiveStatus>().Snapshot();

        Assert.Equal(uuids.Count, stored);
        Assert.NotEmpty(users);
        Assert.NotEmpty(devices);
    }

    [Fact]
    public async Task A_message_that_cannot_be_handled_is_skipped_not_thrown()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var broken = LiveMessage.Parse("""{"event":"CALL_LOG_UPDATED","data":{"records":[{"uuid":42,"time":"never"}]}}""")!;

        await app.Services.GetRequiredService<LiveListener>().HandleAsync(broken, Ct);
    }

    [Fact]
    public async Task An_admin_sees_every_person_and_handset()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PlayCapturedSessionAsync(app);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct);
        var (users, devices) = app.Services.GetRequiredService<LiveStatus>().Snapshot();

        Assert.Equal(users.Count, Count(page, "data-user=\""));
        Assert.Equal(devices.Count, Count(page, "data-device=\""));
    }

    [Fact]
    public async Task A_viewer_sees_only_the_people_and_handsets_their_grants_cover()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        await PlayCapturedSessionAsync(app);
        var (_, devices) = app.Services.GetRequiredService<LiveStatus>().Snapshot();
        var owner = devices.First(d => d.UserUuid is not null).UserUuid!;

        using (var scope = app.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
            var viewer = new AppUser { Id = Guid.NewGuid(), UserName = "watcher", SiteId = site.Id };
            Assert.True((await users.CreateAsync(viewer, "a long enough password")).Succeeded);
            await users.AddToRoleAsync(viewer, Roles.Viewer);
            db.Grants.Add(new Grant { Id = Guid.NewGuid(), SiteId = site.Id, UserId = viewer.Id, Kind = LineKind.User, Key = owner, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(Ct);
        }

        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, "watcher", "a long enough password");
        var page = await browser.GetStringAsync(new Uri("/live", UriKind.Relative), Ct);

        Assert.Equal(1, Count(page, "data-user=\""));
        Assert.Contains($"data-user=\"{owner}\"", page, StringComparison.Ordinal);
        Assert.Equal(devices.Count(d => d.UserUuid == owner), Count(page, "data-device=\""));
    }

    [Fact]
    public void In_an_interactive_circuit_the_scope_comes_from_the_components_user()
    {
        // No HTTP request, as in a Blazor circuit after the first render.
        var site = new CurrentSite { Id = Guid.NewGuid() };
        var holder = new AccessScopeHolder(new HttpContextAccessor(), site);
        var userId = Guid.NewGuid();
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim(AccessScopeHolder.SiteClaim, site.Id.ToString())], "test"));

        Assert.Equal(AccessScope.Nobody, holder.Current);
        holder.UsePrincipal(principal);

        Assert.Equal(AccessScope.ForUser(site.Id, userId, isAdmin: false), holder.Current);
    }

    [Fact]
    public async Task The_websocket_rides_the_signed_in_session_through_the_same_handler()
    {
        await using var console = await FakeLiveConsole.StartAsync(["""{"event":"ONGOING_EVENTS_UPDATE","data":{"ongoing_event_count":"1"}}""", """{"event":"USERS_ON_ACTIVE_CALLS","data":{"user_ids":[]}}"""]);
        using var handler = ConsoleHttp.CreateHandler(null);
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = console.Address };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);

        // Without the session cookie the console refuses the upgrade.
        await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            await foreach (var _ in TalkLive.ReadAsync(console.Address, invoker, Ct))
            {
            }
        });

        await new TalkClient(http).SignInAsync("talkwatch", "pw", Ct);
        var events = new List<string>();
        await foreach (var message in TalkLive.ReadAsync(console.Address, invoker, Ct))
        {
            events.Add(message.Event);
        }

        Assert.Equal(["ONGOING_EVENTS_UPDATE", "USERS_ON_ACTIVE_CALLS"], events);
    }

    // The console is trusted, but a message without end would fill memory before anything read it: the socket gives up.
    [Fact]
    public async Task A_live_message_bigger_than_any_the_console_sends_ends_the_connection()
    {
        await using var console = await FakeLiveConsole.StartAsync([new string('x', (4 * 1024 * 1024) + 1)]);
        using var handler = ConsoleHttp.CreateHandler(null);
        using var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = console.Address };
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        await new TalkClient(http).SignInAsync("talkwatch", "pw", Ct);

        await Assert.ThrowsAsync<TalkApiException>(async () =>
        {
            await foreach (var _ in TalkLive.ReadAsync(console.Address, invoker, Ct))
            {
            }
        });
    }

    private static int Count(string html, string marker) =>
        (html.Length - html.Replace(marker, "", StringComparison.Ordinal).Length) / marker.Length;

    /// <summary>
    /// A real HTTP and WebSocket server standing in for the console: sign-in sets a session cookie, and the live
    /// socket refuses anyone without it, then sends the given messages and closes.
    /// </summary>
    private sealed class FakeLiveConsole : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeLiveConsole(WebApplication app, Uri address)
        {
            _app = app;
            Address = address;
        }

        public Uri Address { get; }

        public static async Task<FakeLiveConsole> StartAsync(IReadOnlyList<string> messages)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.UseWebSockets();
            app.MapPost("/api/auth/login", (HttpContext context) =>
            {
                context.Response.Cookies.Append("TOKEN", "session-1");
                return Results.Ok();
            });
            app.Map("/proxy/talk/", async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest || context.Request.Cookies["TOKEN"] != "session-1")
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                using var socket = await context.WebSockets.AcceptWebSocketAsync();
                foreach (var message in messages)
                {
                    await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
                }

                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new FakeLiveConsole(app, new Uri(address));
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    // The Operator board shows the people a call to one of the numbers can reach, and apart from them, the contacts calls
    // are put through to: not console accounts nothing rings, and not the whole address book.
    [Fact]
    public async Task The_operator_board_narrowed_to_a_number_shows_only_who_a_call_to_it_reaches()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var directory = app.Services.GetRequiredService<LineDirectorySync>();
        await directory.RefreshAsync(Ct);
        app.Services.GetRequiredService<LiveStatus>().SetDirectory(directory.Current);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        async Task<string> BoardFor(string did)
        {
            var html = await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct);
            var token = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
            using var form = new FormUrlEncodedContent([new("Did", did), new("ReturnUrl", "/operator"), new("__RequestVerificationToken", token)]);
            Assert.Equal(System.Net.HttpStatusCode.Redirect, (await browser.PostAsync(new Uri("/account/number", UriKind.Relative), form, Ct)).StatusCode);
            return await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct);
        }

        // Nico's own number: Nico, and not the contact the main line's switchboard puts calls to.
        var own = await BoardFor("+441174960404");
        Assert.Contains("data-person=\"abe3a229-7539-48dd-a8e3-e23fd4d9f72f\"", own, StringComparison.Ordinal);
        Assert.DoesNotContain("data-contact=\"ac7a4901-2722-4e41-9f20-87f95df72cb2\"", own, StringComparison.Ordinal);

        // The main line: its switchboard's contact.
        var main = await BoardFor("+441144960042");
        Assert.Contains("data-contact=\"ac7a4901-2722-4e41-9f20-87f95df72cb2\"", main, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_operator_board_shows_the_people_a_number_reaches_and_the_contacts_calls_go_to_apart()
    {
        await using var app = talkwatch.Create(new FixtureConsole(FixtureConsole.DefaultDirectory));
        var directory = app.Services.GetRequiredService<LineDirectorySync>();
        await directory.RefreshAsync(Ct);
        app.Services.GetRequiredService<LiveStatus>().SetDirectory(directory.Current);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = await browser.GetStringAsync(new Uri("/operator", UriKind.Relative), Ct);

        Assert.Contains("data-person=\"abe3a229-7539-48dd-a8e3-e23fd4d9f72f\"", page, StringComparison.Ordinal); // their own number, and the ring group
        Assert.DoesNotContain("data-person=\"28b32e9d-7f03-48cd-95ae-9a333cbc221d\"", page, StringComparison.Ordinal); // an extension nothing rings
        Assert.Contains("data-team=\"Forwarded to\"", page, StringComparison.Ordinal);
        Assert.Contains("data-contact=\"ac7a4901-2722-4e41-9f20-87f95df72cb2\"", page, StringComparison.Ordinal); // the main line's switchboard puts calls to them
        Assert.DoesNotContain("data-contact=\"7624c978-2957-44f4-a42a-93ccd47a0bb8\"", page, StringComparison.Ordinal); // only in the address book
        Assert.DoesNotContain("data-person=\"ac7a4901-2722-4e41-9f20-87f95df72cb2\"", page, StringComparison.Ordinal);
    }
}

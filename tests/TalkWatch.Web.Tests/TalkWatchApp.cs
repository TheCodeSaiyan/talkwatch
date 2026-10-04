using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>One PostgreSQL container per test class; each app gets a database of its own.</summary>
public sealed partial class TalkWatchApp : IAsyncLifetime
{
    public const string AdminUsername = "admin";
    public const string AdminPassword = "correct horse battery";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine").Build();

    public async ValueTask InitializeAsync() => await _postgres.StartAsync();

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    // In local CI the tests run in a container on the same Docker bridge as the database, and connect to it there
    // directly (TALKWATCH_TEST_DATABASES=container-address): its published port is reachable from inside a container only
    // by way of Docker Desktop's forwarding through Windows, where now and then a connection hung for a minute.
    private string Server() => Environment.GetEnvironmentVariable("TALKWATCH_TEST_DATABASES") == "container-address"
        ? new Npgsql.NpgsqlConnectionStringBuilder(_postgres.GetConnectionString()) { Host = _postgres.IpAddress, Port = PostgreSqlBuilder.PostgreSqlPort }.ConnectionString
        : _postgres.GetConnectionString();

    /// <summary>
    /// The real app, configured only through settings as an install would be, reading from a fake console. The
    /// console URL is left unset so the background poller stays off and tests run polls themselves, one at a time.
    /// </summary>
    public WebApplicationFactory<Program> Create(
        FixtureConsole console, string talkPassword = FixtureConsole.Password, Action<IServiceCollection>? services = null,
        IReadOnlyDictionary<string, string>? settings = null, bool fakeConsole = true)
    {
        // A longer connect timeout than Npgsql's 15 seconds: in local CI, containers are reached through Docker Desktop's
        // port forwarding, which under a full parallel run was once slower than that.
        // Each app gets its own database, so its own connection pool, and idle pooled connections outlive the app: over
        // a class of tests sharing one server they added up past PostgreSQL's 100 ("53300: too many clients"). A small
        // pool whose idle connections close within seconds keeps the total down without opening one per query.
        var database = new Npgsql.NpgsqlConnectionStringBuilder(Server())
        {
            Database = "t" + Guid.NewGuid().ToString("N"), Timeout = 60, MaxPoolSize = 10, ConnectionIdleLifetime = 2, ConnectionPruningInterval = 1,
        }.ConnectionString;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:TalkWatch", database);
            b.UseSetting("Talk:Username", FixtureConsole.Username);
            b.UseSetting("Talk:Password", talkPassword);
            b.UseSetting("Bootstrap:AdminUsername", AdminUsername);
            b.UseSetting("Bootstrap:AdminPassword", AdminPassword);
            b.UseSetting("Audio:Path", Directory.CreateTempSubdirectory("talkwatch-audio-").FullName);
            foreach (var (key, value) in settings ?? new Dictionary<string, string>())
            {
                b.UseSetting(key, value);
            }

            if (fakeConsole)
            {
                b.ConfigureTestServices(s => s.AddHttpClient(TalkSession.HttpClientName, c => c.BaseAddress = new Uri("https://console.test"))
                    .ConfigurePrimaryHttpMessageHandler(() => console));
            }
            if (services is not null)
            {
                b.ConfigureTestServices(services);
            }
        });
    }

    public static HttpClient Browser(WebApplicationFactory<Program> app) =>
        app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });

    /// <summary>Signs in through the real form, antiforgery token and all. Returns where the app sent the browser.</summary>
    public static async Task<HttpResponseMessage> SignInAsync(HttpClient browser, string username, string password, string? returnUrl = null)
    {
        var page = await browser.GetStringAsync(new Uri("/signin", UriKind.Relative));
        var token = AntiforgeryToken().Match(page).Groups[1].Value;
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token),
            ["Username"] = username,
            ["Password"] = password,
            ["ReturnUrl"] = returnUrl ?? "",
        };
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri("/account/signin", UriKind.Relative), content);
    }

    public static int CallRows(string html) => CallRow().Count(html);

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex AntiforgeryToken();

    [GeneratedRegex(@"data-call=""")]
    private static partial Regex CallRow();
}

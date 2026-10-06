using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Web.Components;
using TalkWatch.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Docker secrets: a file named after a setting, with __ for each level, supplies that setting and overrides the
// environment. /run/secrets/Talk__Password becomes Talk:Password. The directory can be moved with TALKWATCH_SECRETS_DIR.
builder.Configuration.AddKeyPerFile(Environment.GetEnvironmentVariable("TALKWATCH_SECRETS_DIR") ?? "/run/secrets", optional: true);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHealthChecks();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.Configure<TalkOptions>(builder.Configuration.GetSection(TalkOptions.Section));
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.Section));
builder.Services.Configure<BootstrapOptions>(builder.Configuration.GetSection(BootstrapOptions.Section));
builder.Services.Configure<AudioOptions>(builder.Configuration.GetSection(AudioOptions.Section));
builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection(SmtpOptions.Section));
builder.Services.Configure<TelegramOptions>(builder.Configuration.GetSection(TelegramOptions.Section));

// Configuration comes from environment variables alone, so a missing database is a start-up error that says what to set.
var connectionString = DatabaseConnection.Build(
    builder.Configuration.GetConnectionString("TalkWatch")
        ?? throw new InvalidOperationException("Set ConnectionStrings__TalkWatch to the PostgreSQL connection string."),
    builder.Configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>()?.Password);

builder.Services.AddSingleton<CurrentSite>();
builder.Services.AddScoped<AccessScopeHolder>();
builder.Services.AddScoped<IAccessScopeSource>(sp => sp.GetRequiredService<AccessScopeHolder>());
builder.Services.AddDbContext<TalkWatchDbContext>(o => o.UseNpgsql(connectionString));

builder.AddKeyRing();

builder.Services.AddIdentity<AppUser, IdentityRole<Guid>>(o =>
    {
        // Length over composition, as NCSC and NIST advise: forced digits and capitals push people to Password1!.
        o.Password.RequiredLength = 12;
        o.Password.RequireNonAlphanumeric = false;
        o.Password.RequireDigit = false;
        o.Password.RequireUppercase = false;
        o.Password.RequireLowercase = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.User.RequireUniqueEmail = false;
    })
    .AddEntityFrameworkStores<TalkWatchDbContext>()
    // Password resets by an admin need a token provider; without one, setting a password failed with a server error.
    .AddDefaultTokenProviders()
    .AddClaimsPrincipalFactory<SiteClaimsFactory>()
    // Two-factor keys and recovery codes are credentials too: kept encrypted, as everything else is.
    .AddUserStore<ProtectedTokenUserStore>();
builder.Services.ConfigureApplicationCookie(o =>
{
    o.LoginPath = "/signin";
    o.AccessDeniedPath = "/signin";
    o.Cookie.Name = "talkwatch";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.SlidingExpiration = true;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
});
builder.AddProtection();
builder.AddOidc();
builder.AddApi();
builder.AddTelemetry();
// What a person may do comes from their role's permissions, carried as claims in the sign-in cookie. Their own alerts
// can also come from a role on a number, read live.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, SiteOrNumberHandler>();
var authorization = builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Permissions.AlertsPolicy, p => p.AddRequirements(new SiteOrNumberRequirement(Permission.OwnAlerts | Permission.ManageAlerts, Permission.OwnAlerts)))
    .AddPolicy(Permissions.ReportsPolicy, p => p.AddRequirements(new SiteOrNumberRequirement(Permission.ManageReports, Permission.ManageReports)))
    .AddPolicy(Permissions.AdminPolicy, p => p.RequireRole(Roles.Admin));
foreach (var permission in Permissions.Each)
{
    authorization.AddPolicy(Permissions.Policy(permission), p => p.RequireAssertion(c => c.User.Can(permission)));
}

// Permissions are read from the person's roles on every request (LivePermissions), so a change to their role, or to a
// role's permissions, holds from their next page without signing them out. The cookie itself is checked against the
// account within a minute: locking someone, or resetting their password or second factor, signs them out then.
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, LivePermissions>();
builder.Services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));
builder.Services.AddScoped<Audit>();
builder.Services.AddScoped<AlertFlowStore>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IngestionStatus>();
builder.Services.AddSingleton(sp => new AudioStore(sp.GetRequiredService<IOptions<AudioOptions>>().Value.Path));
// The console, its credentials and the way to it: the Console page over the Talk__ settings, changeable while running.
builder.Services.AddSingleton<ConsoleConnection>();
builder.Services.AddSingleton<ConsoleTunnel>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ConsoleTunnel>());
builder.Services.AddHttpClient(TalkSession.HttpClientName, client => client.BaseAddress = ConsoleHandler.Placeholder)
    .ConfigurePrimaryHttpMessageHandler(sp => new ConsoleHandler(sp.GetRequiredService<ConsoleConnection>(), sp.GetRequiredService<ConsoleTunnel>()))
    // The console session's cookie lives in this handler. Kept for the life of the app (the factory otherwise rotates
    // handlers every two minutes), so the poller, downloads and the live WebSocket all share one session.
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<TalkSession>();
builder.Services.AddSingleton<PlatformStatus>();
builder.Services.AddSingleton<ConsoleVersionMonitor>();
builder.Services.AddSingleton<LineDirectorySync>();
builder.Services.AddSingleton<AnsweringLineCheck>();
builder.Services.AddSingleton<TranscriptSync>();
builder.Services.AddSingleton<ConsoleHealthMonitor>();
builder.Services.AddSingleton<ReportBuilder>();
builder.Services.AddSingleton<ReportMailer>();
builder.Services.AddSingleton<ReportScheduler>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ReportScheduler>());
builder.Services.AddSingleton<LiveStatus>();
builder.Services.AddSingleton<LiveListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LiveListener>());
builder.Services.AddSingleton<ChannelSecrets>();
builder.Services.AddSingleton<AlertLinks>();
builder.Services.AddSingleton<BrowserAlerts>();
builder.Services.AddSingleton<WebPushSender>();
builder.Services.AddHttpClient(WebPushSender.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
builder.Services.AddSingleton<AlertSettingsStore>();
builder.Services.AddSingleton<MailSender>();
builder.Services.AddSingleton<AlertService>();
// No request logging: a Telegram bot token is part of the URL, and an ntfy topic name is often the only secret it has.
builder.Services.AddHttpClient(AlertDispatcher.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15)).RemoveAllLoggers();
builder.Services.AddSingleton<AlertDispatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertDispatcher>());
builder.Services.AddSingleton<RetentionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RetentionService>());
builder.Services.AddSingleton<CallLogPoller>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CallLogPoller>());

builder.AddDemo();

var app = builder.Build();

await DatabaseStartup.RunAsync(app.Services, CancellationToken.None);

// 'check-audio': the full check with checksums, then exit, 0 when clean. Otherwise a quick one before starting, since
// after a restore the database and the audio files can be from a few minutes apart.
if (args.Contains("check-audio"))
{
    var full = await DatabaseStartup.CheckAudioAsync(app.Services, checksums: true, CancellationToken.None);
    Console.WriteLine($"{full.Checked} copied files checked: {full.Missing} missing, {full.Damaged} damaged (both copied again at the next poll), {full.Orphans.Count} not referred to.");
    foreach (var orphan in full.Orphans)
    {
        Console.WriteLine($"  not referred to: {orphan}");
    }

    return full.Clean ? 0 : 1;
}

await DatabaseStartup.CheckAudioAsync(app.Services, checksums: false, CancellationToken.None);

app.UseProtection();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// No HTTPS redirection here: TLS ends at the reverse proxy in front of the container.
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();
// The demo guest is an admin who may change nothing that would change the site for those after them.
app.UseDemoGuard();
app.UseRateLimiter();
app.UseAntiforgery();

app.MapHealthChecks("/healthz");
app.MapStaticAssets();
app.MapAccount();
app.MapOidc();
app.MapOidcLink();
app.MapAudio();
app.MapAdmin();
app.MapAlertAdmin();
app.MapAlertLinks();
app.MapRetention();
app.MapConsole();
app.MapApi();
app.MapExport();
app.MapCallBacks();
app.MapAnsweringLines();
app.MapNumbers();
app.MapReports();
app.MapBrowserAlerts();
app.MapSearch();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Makes and checks personal API tokens. A token is 'tw_' and 32 random bytes; only its hash is kept.</summary>
public static class ApiTokens
{
    public const string Scheme = "ApiToken";
    public const string Policy = "Api";
    public const string Prefix = "tw_";

    public static string NewToken() => Prefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

/// <summary>
/// Signs a request in as the owner of the bearer token it carries. The token is looked up in a scope of its own, so the
/// request's scope is only ever the owner's. A locked account's tokens stop working with it.
/// </summary>
public sealed class ApiTokenHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder,
    IServiceScopeFactory scopes, TimeProvider clock)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + ApiTokens.Prefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var hash = ApiTokens.Hash(header["Bearer ".Length..].Trim());
        var token = await db.ApiTokens.SingleOrDefaultAsync(t => t.Hash == hash, Context.RequestAborted);
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (token is null || await users.FindByIdAsync(token.UserId.ToString()) is not { } user)
        {
            return AuthenticateResult.Fail("The token is not valid.");
        }

        if (await users.IsLockedOutAsync(user))
        {
            return AuthenticateResult.Fail("The token's account is locked.");
        }

        // A minute's grain is enough to say when a token was last used, and spares a write on every request.
        var now = clock.GetUtcNow();
        if (token.LastUsedAt is null || now - token.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            token.LastUsedAt = now;
            await db.SaveChangesAsync(Context.RequestAborted);
        }

        // The number chosen in the rail's switcher is for the owner's pages: a script's answers would otherwise change
        // with whatever they last looked at in the browser. A token sees everything its owner is granted.
        var principal = await scope.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<AppUser>>().CreateAsync(user);
        var identity = new ClaimsIdentity(principal.Claims.Where(c => c.Type != AccessScopeHolder.DidClaim), ApiTokens.Scheme);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), ApiTokens.Scheme));
    }
}

/// <summary>
/// The read-only API, by token only. Every query goes through the token owner's access scope, as their pages do: the
/// API cannot show a call, line or figure their grants do not cover.
/// </summary>
public static class ApiEndpoints
{
    public const int MaxPageSize = 500;

    public static void AddApi(this WebApplicationBuilder builder)
    {
        builder.Services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiTokenHandler>(ApiTokens.Scheme, null);
        builder.Services.AddAuthorizationBuilder().AddPolicy(ApiTokens.Policy, p => p.AddAuthenticationSchemes(ApiTokens.Scheme).RequireAuthenticatedUser());
    }

    public static void MapApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization(ApiTokens.Policy).WithMetadata(new Microsoft.AspNetCore.Mvc.SkipStatusCodePagesAttribute());

        // Newest first. Instants in ISO 8601; the last seven days when none are given.
        api.MapGet("/calls", async (DateTimeOffset? since, DateTimeOffset? until, int? page, int? pageSize, TalkWatchDbContext db, TimeProvider clock) =>
        {
            var end = (until ?? clock.GetUtcNow()).ToUniversalTime();
            var start = (since ?? end.AddDays(-7)).ToUniversalTime();
            var size = Math.Clamp(pageSize ?? 100, 1, MaxPageSize);
            var number = Math.Max(page ?? 0, 0);
            var query = db.Calls.AsNoTracking().Where(c => c.Time >= start && c.Time < end);
            var total = await query.CountAsync();
            var calls = await query.OrderByDescending(c => c.Time).Skip(number * size).Take(size).Include(c => c.Lines).ToListAsync();
            return Results.Ok(new { since = start, until = end, page = number, pageSize = size, total, calls = calls.Select(Shape) });
        });

        api.MapGet("/calls/{uuid}", async (string uuid, TalkWatchDbContext db) =>
            await db.Calls.AsNoTracking().Include(c => c.Lines).Include(c => c.Events).SingleOrDefaultAsync(c => c.TalkUuid == uuid) is { } call
                ? Results.Ok(new { call = Shape(call), events = call.Events.OrderBy(e => e.Sequence).Select(e => new { e.Time, e.Event }) })
                : Results.NotFound());

        api.MapGet("/lines", async (TalkWatchDbContext db) =>
            Results.Ok(await db.Lines.AsNoTracking().OrderBy(l => l.Kind).ThenBy(l => l.Name)
                .Select(l => new { kind = l.Kind.ToString(), l.Key, l.Name, l.Ext, l.Present }).ToListAsync()));

        // Whole days in the site's zone, as on the dashboard.
        api.MapGet("/stats", async (DateOnly from, DateOnly to, TalkWatchDbContext db, IOptions<SiteOptions> site) =>
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(site.Value.TimeZone);
            var (start, end) = (from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue).AddDays(1));
            try
            {
                var s = await CallStatistics.ComputeAsync(db, new(start, zone.GetUtcOffset(start)), new(end, zone.GetUtcOffset(end)), zone, CancellationToken.None);
                return Results.Ok(new
                {
                    from, to, inbound = s.Inbound, answerRate = s.AnswerRate, averageAnsweredSeconds = (int)s.AverageAnswered.TotalSeconds,
                    byOutcome = s.ByOutcome.ToDictionary(o => o.Key.ToString(), o => o.Value),
                    inboundByHour = s.InboundByHour,
                    byDay = s.ByDay,
                    byLine = s.ByLine.Select(l => new { kind = l.Kind.ToString(), l.Key, l.Name, l.Inbound, l.Answered, l.Missed, l.Voicemail, l.HungUpAtSwitchboard }),
                });
            }
            catch (ArgumentException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
        });
    }

    private static object Shape(CallRow c) => new
    {
        uuid = c.TalkUuid, time = c.Time, direction = c.Direction, outcome = c.Outcome.ToString(), status = c.Status,
        from = c.FromRaw, fromE164 = c.FromE164, to = c.ToRaw, toE164 = c.ToE164, answeredBy = c.AnsweredByRaw,
        durationSeconds = c.DurationSeconds, callerName = c.CallerName, hasRecording = c.HasRecording,
        lines = c.Lines.Select(l => new { kind = l.Kind.ToString(), l.Key }),
    };
}

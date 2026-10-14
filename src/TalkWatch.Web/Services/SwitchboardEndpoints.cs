using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

public static class SwitchboardEndpoints
{
    public static void MapSwitchboard(this IEndpointRouteBuilder app)
    {
        // Plays a switchboard node's greeting, fetched from the console as GetSwitchboardAudio does to time it. Anyone
        // signed in may: it is what every caller hears. Every play is written to the audit log, as recordings are.
        app.MapGet("/switchboard/greeting/{nodeId}", async (string nodeId, TalkWatchDbContext db, TalkSession session, CurrentSite site, TimeProvider clock, HttpContext http) =>
        {
            var file = await db.SwitchboardNodes.AsNoTracking()
                .Where(n => n.NodeId == nodeId && n.Present && n.GreetingFile != null).Select(n => n.GreetingFile).SingleOrDefaultAsync(http.RequestAborted);
            if (file is null)
            {
                return Results.NotFound();
            }

            byte[]? audio;
            try
            {
                if (!await session.EnsureSignedInAsync(http.RequestAborted))
                {
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                audio = await session.Client.GetSwitchboardAudioAsync(file, http.RequestAborted);
            }
            catch (Exception e) when (e is TalkApiException or HttpRequestException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            if (audio is null)
            {
                return Results.NotFound();
            }

            // One play is the request that starts at the beginning, as for recordings.
            var range = http.Request.Headers.Range.ToString();
            if (range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.Ordinal))
            {
                db.AuditEvents.Add(new AuditEvent
                {
                    Id = Guid.NewGuid(),
                    SiteId = site.Id,
                    At = clock.GetUtcNow(),
                    UserId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
                    Action = "greeting.play",
                    TargetType = "switchboard-node",
                    TargetId = nodeId,
                });
                await db.SaveChangesAsync(http.RequestAborted);
            }

            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(audio, "audio/mpeg", enableRangeProcessing: true);
        }).RequireAuthorization();
    }
}

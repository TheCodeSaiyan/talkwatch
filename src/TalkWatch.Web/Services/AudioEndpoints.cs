using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

public static class AudioEndpoints
{
    public static void MapAudio(this IEndpointRouteBuilder app)
    {
        // Streams a copied recording or voicemail to someone allowed to hear it. The lookup goes through the signed-in
        // person's access scope, so audio they may not hear is simply not found: 404, which also does not reveal that
        // it exists. Every play is written to the audit log. Range requests let the browser seek.
        app.MapGet("/audio/{id:guid}", async (Guid id, TalkWatchDbContext db, AudioStore store, CurrentSite site, TimeProvider clock, HttpContext http) =>
        {
            var audio = await db.AudioFiles.AsNoTracking()
                .SingleOrDefaultAsync(a => a.Id == id && a.State == AudioState.Copied && a.RelativePath != null);
            if (audio is null)
            {
                return Results.NotFound();
            }

            // A player fetches audio in several ranges while playing and seeking; one play is the request that starts
            // at the beginning.
            var range = http.Request.Headers.Range.ToString();
            if (range.Length == 0 || range.StartsWith("bytes=0-", StringComparison.Ordinal))
            {
                db.AuditEvents.Add(new AuditEvent
                {
                Id = Guid.NewGuid(),
                SiteId = site.Id,
                At = clock.GetUtcNow(),
                UserId = Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId) ? userId : null,
                Action = audio.Kind == AudioKind.Recording ? "recording.play" : "voicemail.play",
                TargetType = "audio",
                TargetId = audio.Id.ToString(),
                });
                await db.SaveChangesAsync();
            }

            http.Response.Headers.CacheControl = "private, no-store";
            return Results.File(store.OpenRead(audio.RelativePath!), audio.ContentType ?? "audio/mpeg", enableRangeProcessing: true);
        }).RequireAuthorization();
    }
}

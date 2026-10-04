using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Someone turning alerts in their own browser on or off, and allowing desktop notifications in a browser. Anyone
/// signed in may: a flow decides what reaches them, and their grants what they may hear of.
/// </summary>
public static class BrowserAlertEndpoints
{
    public sealed record SubscriptionForm(string? Endpoint, SubscriptionKeys? Keys);

    public sealed record SubscriptionKeys(string? P256dh, string? Auth);

    public static void MapBrowserAlerts(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").RequireAuthorization().WithMetadata(new SkipStatusCodePagesAttribute());

        account.MapPost("/browser-alerts", async (TalkWatchDbContext db, CurrentSite site, Audit audit, TimeProvider clock, HttpContext http) =>
        {
            var me = Me(http);
            var mine = await db.AlertChannels.Where(c => c.Kind == ChannelKind.Browser && c.OwnerUserId == me).ToListAsync();
            if (mine.Count > 0)
            {
                db.AlertChannels.RemoveRange(mine);
                await db.SaveChangesAsync();
                await audit.WriteAsync("alert.channel.remove", "alert_channel", mine[0].Id, "browser");
                return Results.Redirect("/account?msg=" + Uri.EscapeDataString("Alerts in TalkWatch turned off."));
            }

            var channel = new AlertChannel
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = $"{http.User.Identity?.Name}'s browser", Kind = ChannelKind.Browser, Target = "",
                OwnerUserId = me, CreatedAt = clock.GetUtcNow(),
            };
            db.AlertChannels.Add(channel);
            await db.SaveChangesAsync();
            await audit.WriteAsync("alert.channel.add", "alert_channel", channel.Id, "browser");
            return Results.Redirect("/account?msg=" + Uri.EscapeDataString("Alerts in TalkWatch turned on: flows that notify you now reach the bell."));
        });

        account.MapGet("/push/key", async (WebPushSender push, CancellationToken cancellationToken) =>
            Results.Json(new { key = await push.PublicKeyAsync(cancellationToken) }));

        // JSON, so another site cannot post it from a form; the cookie is SameSite=Strict as well.
        account.MapPost("/push", async ([FromBody] SubscriptionForm form, TalkWatchDbContext db, CurrentSite site, TimeProvider clock, HttpContext http) =>
        {
            if (!Uri.TryCreate(form.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps
                || string.IsNullOrEmpty(form.Keys?.P256dh) || string.IsNullOrEmpty(form.Keys.Auth))
            {
                return Results.BadRequest();
            }

            // A browser is one person's: signing in as someone else there moves it to them.
            var existing = await db.PushSubscriptions.IgnoreQueryFilters().SingleOrDefaultAsync(p => p.Endpoint == form.Endpoint);
            if (existing is not null)
            {
                db.PushSubscriptions.Remove(existing);
            }

            db.PushSubscriptions.Add(new PushSubscriptionRow
            {
                Id = Guid.NewGuid(), SiteId = site.Id, UserId = Me(http), Endpoint = form.Endpoint!, P256dh = form.Keys.P256dh, Auth = form.Keys.Auth, CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        account.MapPost("/push/remove", async ([FromBody] SubscriptionForm form, TalkWatchDbContext db) =>
        {
            await db.PushSubscriptions.Where(p => p.Endpoint == form.Endpoint).ExecuteDeleteAsync();
            return Results.NoContent();
        });
    }

    private static Guid Me(HttpContext http) => Guid.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

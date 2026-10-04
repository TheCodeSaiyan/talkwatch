using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Marking a missed caller done: dealt with some other way, or not worth ringing back. Anyone who can see the calls may,
/// since returning calls is the work of whoever answers the phone; it is audited, and acknowledges the calls' alerts.
/// </summary>
public static class CallBackEndpoints
{
    public sealed class DoneForm
    {
        /// <summary>The caller's number in E.164, as the call-back list gives it.</summary>
        public string? Number { get; set; }
    }

    public sealed class AssignForm
    {
        /// <summary>The caller's number in E.164, as the call-back list gives it.</summary>
        public string? Number { get; set; }

        /// <summary>Who returns the call: a person's id, or empty to leave it with nobody in particular.</summary>
        public string? Person { get; set; }
    }

    /// <summary>
    /// The missed calls from a number this person may act on: those they can see, and without the permission site-wide,
    /// only those on numbers where a role of theirs allows it. Null when they may not act on call-backs at all.
    /// </summary>
    private static async Task<IQueryable<CallRow>?> ActionableAsync(TalkWatchDbContext db, HttpContext http, string? number, CancellationToken cancellationToken)
    {
        var calls = db.Calls.Returnable().Where(c => c.FromE164 == number && c.ReturnedAt == null);
        if (http.User.Can(Permission.MarkCallBacks))
        {
            return calls;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var me)
            || !await NumberAccess.AnywhereAsync(db, me, Permission.MarkCallBacks, cancellationToken))
        {
            return null;
        }

        return calls.OnNumbers(db, NumberAccess.NumbersWith(db, me, Permission.MarkCallBacks));
    }

    public static void MapCallBacks(this IEndpointRouteBuilder app)
    {
        // A 404 stays a 404, rather than being re-run through the not-found page, whose antiforgery check turns it into a 400.
        var group = app.MapGroup("/callbacks").RequireAuthorization().WithMetadata(new SkipStatusCodePagesAttribute());

        group.MapPost("/done", async ([FromForm] DoneForm form, TalkWatchDbContext db, AlertService alerts, Audit audit, TimeProvider clock, HttpContext http, CancellationToken cancellationToken) =>
        {
            // Through the person's access scope: only the missed calls from this number that they can see, and without
            // the permission site-wide, only those on numbers where a role of theirs allows marking them.
            if (await ActionableAsync(db, http, form.Number, cancellationToken) is not { } calls)
            {
                return Results.Forbid();
            }

            var open = await calls.ToListAsync(cancellationToken);
            if (open.Count == 0)
            {
                return Results.NotFound();
            }

            var by = http.User.Identity?.Name ?? "someone";
            var now = clock.GetUtcNow();
            foreach (var call in open)
            {
                (call.ReturnedAt, call.ReturnedHow, call.ReturnedBy) = (now, CallBackHow.MarkedDone, by);
            }

            await db.SaveChangesAsync(cancellationToken);
            var ids = open.Select(c => c.Id).ToList();
            foreach (var alert in await db.AlertEvents.IgnoreQueryFilters().Where(e => e.CallId != null && ids.Contains(e.CallId.Value) && e.AcknowledgedAt == null).Select(e => e.Id).ToListAsync(cancellationToken))
            {
                await alerts.AcknowledgeAsync(alert, by, cancellationToken);
            }

            foreach (var call in open)
            {
                await audit.WriteAsync("callback.done", "call", call.Id, null);
            }

            return Results.Redirect($"/callbacks?msg={Uri.EscapeDataString($"Marked done: {open.Count} missed call{(open.Count == 1 ? "" : "s")}.")}");
        });

        // Who returns a caller, chosen by hand: as a flow's Assign step does, or back to nobody in particular.
        group.MapPost("/assign", async ([FromForm] AssignForm form, TalkWatchDbContext db, CurrentSite site, Audit audit, TimeProvider clock, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (await ActionableAsync(db, http, form.Number, cancellationToken) is not { } calls)
            {
                return Results.Forbid();
            }

            AppUser? person = null;
            if (!string.IsNullOrEmpty(form.Person))
            {
                person = Guid.TryParse(form.Person, out var id) ? await db.Users.SingleOrDefaultAsync(u => u.Id == id && u.SiteId == site.Id, cancellationToken) : null;
                if (person is null)
                {
                    return Results.Redirect($"/callbacks?msg={Uri.EscapeDataString("Choose someone to return the call.")}");
                }
            }

            var open = await calls.ToListAsync(cancellationToken);
            if (open.Count == 0)
            {
                return Results.NotFound();
            }

            var now = clock.GetUtcNow();
            foreach (var call in open)
            {
                (call.CallBackAssignedTo, call.CallBackAssignedAt) = (person?.Id, person is null ? null : now);
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var call in open)
            {
                await audit.WriteAsync("callback.assign", "call", call.Id, person?.UserName ?? "nobody");
            }

            var said = person is null ? "No longer assigned to anyone." : $"Assigned to {person.UserName}.";
            return Results.Redirect($"/callbacks?msg={Uri.EscapeDataString(said)}");
        });
    }
}

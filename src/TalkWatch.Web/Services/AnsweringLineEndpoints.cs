using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Ticking an outside Contact as an answering line, with its greeting phrases; and correcting what a call it answered
/// turned out to be. Ticking changes alerting for the whole site, so it is for those who manage alerts; correcting a
/// call is for those who may mark call-backs on it.
/// </summary>
public static class AnsweringLineEndpoints
{
    public sealed class LineForm
    {
        public string? ContactId { get; set; }

        public bool Ticked { get; set; }

        public string? Phrases { get; set; }
    }

    public sealed class FindingForm
    {
        /// <summary>person, message or hungup.</summary>
        public string? Finding { get; set; }
    }

    public static void MapAnsweringLines(this IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/alerts/answering-lines", async ([FromForm] LineForm form, TalkWatchDbContext db, CurrentSite site, LineDirectorySync directory, Audit audit,
            TimeProvider clock, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (!http.User.Can(Permission.ManageAlerts))
            {
                return Results.NotFound();
            }

            var contact = directory.Current.Contacts.FirstOrDefault(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == form.ContactId);
            if (contact is null)
            {
                return Back("That contact is not in Talk any more.");
            }

            var phrases = string.Join('\n', AnsweringLine.PhrasesOf(form.Phrases ?? ""));
            var line = await db.AnsweringLines.SingleOrDefaultAsync(a => a.ContactId == form.ContactId, cancellationToken);
            if (!form.Ticked)
            {
                if (line is not null)
                {
                    db.AnsweringLines.Remove(line);
                    await db.SaveChangesAsync(cancellationToken);
                    await audit.WriteAsync("answering_line.remove", "contact", site.Id, contact.DisplayName);
                }

                return Back($"Calls {contact.DisplayName} answers are no longer checked for its voicemail.");
            }

            if (phrases.Length == 0)
            {
                return Back($"Give at least one phrase from {contact.DisplayName}'s greeting, such as \"is not available\".");
            }

            line ??= db.AnsweringLines.Add(new AnsweringLine { SiteId = site.Id, ContactId = form.ContactId! }).Entity;
            (line.Phrases, line.UpdatedAt) = (phrases, clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("answering_line.set", "contact", site.Id, $"{contact.DisplayName}: {phrases.Replace('\n', '|')}");
            return Back($"Calls {contact.DisplayName} answers are checked against {AnsweringLine.PhrasesOf(phrases).Count} greeting phrase(s) once their transcript arrives. Reprocess to apply them to calls it has already answered.", form.ContactId);
        }).RequireAuthorization(Permissions.AlertsPolicy).WithMetadata(new SkipStatusCodePagesAttribute());

        // Every past call the contact answered, read again against its phrases as they are now.
        app.MapPost("/admin/alerts/answering-lines/reprocess", async ([FromForm] LineForm form, LineDirectorySync directory, AnsweringLineCheck check, Audit audit,
            CurrentSite site, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (!http.User.Can(Permission.ManageAlerts))
            {
                return Results.NotFound();
            }

            var contact = directory.Current.Contacts.FirstOrDefault(c => c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == form.ContactId);
            if (contact is null || form.ContactId is null)
            {
                return Back("That contact is not in Talk any more.");
            }

            var done = await check.ReprocessAsync(form.ContactId, TranscriptSync.RecentEnoughToAlert, cancellationToken);
            await audit.WriteAsync("answering_line.reprocess", "contact", site.Id, $"{contact.DisplayName}: {done.Calls} calls");
            if (done.Calls == 0)
            {
                return Back($"{contact.DisplayName} has answered no calls to reprocess.", form.ContactId);
            }

            var parts = new List<string>();
            void Say(int count, string what)
            {
                if (count > 0)
                {
                    parts.Add($"{count} {what}");
                }
            }

            Say(done.MessageLeft, "left a message");
            Say(done.HungUp, "hung up at the greeting");
            Say(done.Person, "reached a person");
            Say(done.TooShort, "too short to tell");
            Say(done.NoTranscript, "with no transcript");
            Say(done.ByHand, "set by hand, left alone");
            return Back($"Reprocessed {done.Calls} call{(done.Calls == 1 ? "" : "s")} {contact.DisplayName} answered: {string.Join(", ", parts)}.", form.ContactId);
        }).RequireAuthorization(Permissions.AlertsPolicy).WithMetadata(new SkipStatusCodePagesAttribute());

        app.MapPost("/calls/{uuid}/answering", async (string uuid, [FromForm] FindingForm form, TalkWatchDbContext db, AnsweringLineCheck check, Audit audit,
            HttpContext http, CancellationToken cancellationToken) =>
        {
            OutsideFinding? finding = form.Finding switch
            {
                "person" => OutsideFinding.Person,
                "message" => OutsideFinding.MessageLeft,
                "hungup" => OutsideFinding.HungUpAtGreeting,
                _ => null,
            };

            // Through the person's own view: a call they cannot see is not there for them.
            var calls = db.Calls.Where(c => c.TalkUuid == uuid);
            if (!http.User.Can(Permission.MarkCallBacks))
            {
                if (!Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var me))
                {
                    return Results.Forbid();
                }

                calls = calls.OnNumbers(db, NumberAccess.NumbersWith(db, me, Permission.MarkCallBacks));
            }

            var call = await calls.Select(c => new { c.Id }).SingleOrDefaultAsync(cancellationToken);
            if (call is null || finding is null)
            {
                return Results.NotFound();
            }

            var who = http.User.Identity?.Name ?? "someone";
            await check.MarkAsync(call.Id, finding.Value, who, TranscriptSync.RecentEnoughToAlert, cancellationToken);
            await audit.WriteAsync("call.answering", "call", call.Id, finding.Value.ToString());
            var said = finding switch
            {
                OutsideFinding.MessageLeft => "Marked as a message left with the answering line.",
                OutsideFinding.HungUpAtGreeting => "Marked as hung up at the answering line's greeting.",
                _ => "Marked as answered by a person.",
            };
            return Results.Redirect($"/calls/{Uri.EscapeDataString(uuid)}?msg={Uri.EscapeDataString(said)}");
        }).RequireAuthorization().WithMetadata(new SkipStatusCodePagesAttribute());
    }

    // Back to the page, opened at the contact when there is one.
    private static IResult Back(string message, string? contact = null) =>
        Results.Redirect($"/admin/answering-lines?msg={Uri.EscapeDataString(message)}{(contact is null ? "" : $"&open={Uri.EscapeDataString(contact)}#contact-{Uri.EscapeDataString(contact)}")}");
}

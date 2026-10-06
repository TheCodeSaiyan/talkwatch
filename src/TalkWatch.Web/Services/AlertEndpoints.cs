using System.Security.Claims;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Channels and flows for alerts, for admins and Managers. Every change is written to the audit log.</summary>
public static partial class AlertEndpoints
{
    public sealed class ChannelForm
    {
        public string? Name { get; set; }
        public ChannelKind Kind { get; set; }
        public string? Target { get; set; }
        public string? Secret { get; set; }
        /// <summary>A person's id, or empty for a site channel. A string, because the form sends empty for none.</summary>
        public string? Owner { get; set; }

        public List<DayOfWeek>? QuietDays { get; set; }
        public string? QuietStart { get; set; }
        public string? QuietEnd { get; set; }
    }

    /// <summary>Empty text means "use the Smtp__ or Telegram__ setting". An empty secret keeps the saved one.</summary>
    public sealed class TestEmailForm
    {
        public string? To { get; set; }
    }

    public sealed class SettingsForm
    {
        public string? SmtpHost { get; set; }
        public string? SmtpPort { get; set; }
        public string? SmtpFrom { get; set; }
        public string? SmtpUsername { get; set; }
        public string? SmtpPassword { get; set; }
        public bool ClearSmtpPassword { get; set; }

        /// <summary>"yes", "no", or empty for the Smtp__StartTls setting.</summary>
        public string? SmtpStartTls { get; set; }

        public string? TelegramBotToken { get; set; }
        public bool ClearTelegramBotToken { get; set; }
        public string? TelegramChatId { get; set; }
    }

    /// <summary>A flow as JSON (<see cref="Flows.Write"/>), with its name; an id to change one, none for a new one.</summary>
    public sealed class FlowForm
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Definition { get; set; }
    }

    public static void MapAlertAdmin(this IEndpointRouteBuilder app)
    {
        // A 404 stays a 404, rather than being re-run through the not-found page, whose antiforgery check turns it into a 400.
        // Admins, and Managers for their own lines: the database filters show a Manager only their own channels,
        // flows and alerts, so a Manager's request for anyone else's finds nothing and answers 404.
        var alerts = app.MapGroup("/admin/alerts").RequireAuthorization(Permissions.AlertsPolicy).WithMetadata(new SkipStatusCodePagesAttribute());

        alerts.MapPost("/channels", async ([FromForm] ChannelForm form, TalkWatchDbContext db, CurrentSite site, ChannelSecrets secrets, AlertSettingsStore settings, Audit audit, TimeProvider clock, HttpContext http, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(form.Name) || !Enum.IsDefined(form.Kind))
            {
                return Refused("Give the channel a name.", form);
            }

            if (TargetProblem(form, await settings.TelegramAsync(cancellationToken)) is { } problem)
            {
                return Refused(problem, form);
            }

            if (!TryWindow(form.QuietDays, form.QuietStart, form.QuietEnd, out var quietDays, out var quietStart, out var quietEnd))
            {
                return Refused("Quiet hours need a start and an end time, and they cannot be the same.", form);
            }

            // A Manager's channels are theirs, whoever the form names: they hear only of the Manager's own lines.
            Guid? owner = IsAdmin(http) ? null : UserId(http);
            if (IsAdmin(http) && !string.IsNullOrEmpty(form.Owner))
            {
                if (!Guid.TryParse(form.Owner, out var id) || !await db.Users.AnyAsync(u => u.Id == id && u.SiteId == site.Id, cancellationToken))
                {
                    return Refused("That person is not in this site.", form);
                }

                owner = id;
            }

            var channel = new AlertChannel
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = form.Name.Trim(), Kind = form.Kind, Target = form.Target?.Trim() ?? "",
                ProtectedSecret = form.Kind == ChannelKind.Email ? null : secrets.Protect(form.Secret), OwnerUserId = owner,
                QuietDays = quietDays, QuietStart = quietStart, QuietEnd = quietEnd, CreatedAt = clock.GetUtcNow(),
            };
            db.AlertChannels.Add(channel);
            await db.SaveChangesAsync(cancellationToken);
            await audit.WriteAsync("alert.channel.add", "alert_channel", channel.Id, $"{channel.Name} {channel.Kind}");
            return Back("Channel added. Send a test to check it arrives.");
        });

        alerts.MapPost("/channels/{id:guid}/test", async (Guid id, TalkWatchDbContext db, CurrentSite site, AlertDispatcher dispatcher, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            var channel = await db.AlertChannels.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (channel is null)
            {
                return Results.NotFound();
            }

            var test = new AlertEvent
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Type = AlertEventType.InboundCall, Key = "test", At = clock.GetUtcNow(),
                Title = "TalkWatch test", Message = $"A test alert for the '{channel.Name}' channel.",
            };
            try
            {
                await dispatcher.SendAsync(channel, test, null, Guid.NewGuid(), stage: 0, urgent: false, cancellationToken);
                return Back($"Test sent to {channel.Name}.");
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or SmtpException)
            {
                return Back($"The test to {channel.Name} failed: {e.Message}");
            }
        });

        alerts.MapPost("/channels/{id:guid}/toggle", async (Guid id, TalkWatchDbContext db, Audit audit) =>
        {
            var channel = await db.AlertChannels.SingleOrDefaultAsync(c => c.Id == id);
            if (channel is null)
            {
                return Results.NotFound();
            }

            channel.Enabled = !channel.Enabled;
            await db.SaveChangesAsync();
            await audit.WriteAsync(channel.Enabled ? "alert.channel.on" : "alert.channel.off", "alert_channel", channel.Id, channel.Name);
            return Back(channel.Enabled ? $"{channel.Name} turned on." : $"{channel.Name} turned off.");
        });

        alerts.MapPost("/channels/{id:guid}/delete", async (Guid id, TalkWatchDbContext db, Audit audit) =>
        {
            var channel = await db.AlertChannels.SingleOrDefaultAsync(c => c.Id == id);
            if (channel is null)
            {
                return Results.NotFound();
            }

            db.AlertChannels.Remove(channel);
            await db.SaveChangesAsync();
            await audit.WriteAsync("alert.channel.remove", "alert_channel", channel.Id, channel.Name);
            return Back($"{channel.Name} removed.");
        });

        alerts.MapPost("/flows", async ([FromForm] FlowForm form, AlertFlowStore store, HttpContext http, CancellationToken cancellationToken) =>
        {
            Guid? id = null;
            if (!string.IsNullOrEmpty(form.Id))
            {
                if (!Guid.TryParse(form.Id, out var parsed))
                {
                    return Results.NotFound();
                }

                id = parsed;
            }

            var (saved, problem) = await store.SaveAsync(id, form.Name, Flows.Read(form.Definition), http.User, cancellationToken);
            return FlowsBack(saved is null ? problem! : "Flow saved.");
        });

        alerts.MapPost("/flows/{id:guid}/toggle", async (Guid id, TalkWatchDbContext db, Audit audit) =>
        {
            var flow = await db.AlertFlows.SingleOrDefaultAsync(f => f.Id == id);
            if (flow is null)
            {
                return Results.NotFound();
            }

            flow.Enabled = !flow.Enabled;
            await db.SaveChangesAsync();
            await audit.WriteAsync(flow.Enabled ? "alert.flow.on" : "alert.flow.off", "alert_flow", flow.Id, flow.Name);
            return FlowsBack(flow.Enabled ? $"{flow.Name} turned on." : $"{flow.Name} turned off: alerts it has started stop at their next step.");
        });

        alerts.MapPost("/flows/{id:guid}/delete", async (Guid id, TalkWatchDbContext db, Audit audit) =>
        {
            var flow = await db.AlertFlows.SingleOrDefaultAsync(f => f.Id == id);
            if (flow is null)
            {
                return Results.NotFound();
            }

            db.AlertFlows.Remove(flow);
            await db.SaveChangesAsync();
            await audit.WriteAsync("alert.flow.remove", "alert_flow", flow.Id, flow.Name);
            return FlowsBack($"{flow.Name} removed.");
        });

        alerts.MapPost("/settings", async ([FromForm] SettingsForm form, TalkWatchDbContext db, CurrentSite site, ChannelSecrets secrets, Audit audit, TimeProvider clock, HttpContext http) =>
        {
            if (!IsAdmin(http))
            {
                return Results.NotFound();
            }

            // The mail server carries every report and alert, and the settings hold the site's mail password and bot
            // token: like the console's, they are for admins, whatever else a role may manage.
            if (!http.User.IsInRole(Roles.Admin))
            {
                return BackToSettings("Only an admin can change the mail and Telegram settings.");
            }

            static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

            int? port = null;
            if (Text(form.SmtpPort) is { } portText)
            {
                if (!int.TryParse(portText, System.Globalization.CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
                {
                    return BackToSettings("The mail server port is a number from 1 to 65535.");
                }

                if (SmtpOptions.PortProblem(p) is { } why)
                {
                    return BackToSettings(why);
                }

                port = p;
            }

            if (Text(form.SmtpFrom) is { } from && !MailAddress.TryCreate(from, out _))
            {
                return BackToSettings("The From address is not an email address.");
            }

            if (Text(form.TelegramChatId) is { } chat && !TelegramChat().IsMatch(chat))
            {
                return BackToSettings("The Telegram chat id is a number such as -1001234567890, or @channelname.");
            }

            if (Text(form.TelegramBotToken) is { } token && !TelegramToken().IsMatch(token))
            {
                return BackToSettings("The bot token from @BotFather is digits, a colon, then letters.");
            }

            var saved = await db.AlertSettings.SingleOrDefaultAsync();
            if (saved is null)
            {
                saved = new AlertSettings { SiteId = site.Id };
                db.AlertSettings.Add(saved);
            }

            saved.SmtpHost = Text(form.SmtpHost);
            saved.SmtpPort = port;
            saved.SmtpFrom = Text(form.SmtpFrom);
            saved.SmtpUsername = Text(form.SmtpUsername);
            saved.SmtpStartTls = form.SmtpStartTls switch { "yes" => true, "no" => false, _ => null };
            saved.TelegramChatId = Text(form.TelegramChatId);
            if (form.ClearSmtpPassword)
            {
                saved.SmtpProtectedPassword = null;
            }
            else if (!string.IsNullOrEmpty(form.SmtpPassword))
            {
                saved.SmtpProtectedPassword = secrets.Protect(form.SmtpPassword);
            }

            if (form.ClearTelegramBotToken)
            {
                saved.TelegramProtectedBotToken = null;
            }
            else if (Text(form.TelegramBotToken) is { } newToken)
            {
                saved.TelegramProtectedBotToken = secrets.Protect(newToken);
            }

            saved.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync();

            // Which secrets are saved here, never their values.
            await audit.WriteAsync("alert.settings", "alert_settings", site.Id,
                $"smtp password {(saved.SmtpProtectedPassword is null ? "not saved" : "saved")}, telegram token {(saved.TelegramProtectedBotToken is null ? "not saved" : "saved")}");
            return BackToSettings("Settings saved. They apply to the next alert sent.");
        });

        // A test email through the saved settings, sent now, with the server's own answer on the page: no waiting for an
        // alert or a report's next try to find out whether email works.
        alerts.MapPost("/settings/test", async ([FromForm] TestEmailForm form, MailSender mailer, UserManager<AppUser> users, CurrentSite site, Audit audit, HttpContext http,
            CancellationToken cancellationToken) =>
        {
            if (!IsAdmin(http))
            {
                return Results.NotFound();
            }

            var to = string.IsNullOrWhiteSpace(form.To) ? (await users.GetUserAsync(http.User))?.Email : form.To.Trim();
            if (to is null || !MailAddress.TryCreate(to, out _))
            {
                return BackToSettings("Give an email address to send the test to.");
            }

            try
            {
                var server = await mailer.OptionsAsync(cancellationToken);
                using var mail = new MailMessage(new MailAddress(server.From, "TalkWatch"), new MailAddress(to))
                {
                    Subject = "TalkWatch test email",
                    Body = $"This is a test from TalkWatch. Email alerts and reports can reach this address through {server.Host}.\n\nSent by TalkWatch.",
                };
                await mailer.SendAsync(server, mail, cancellationToken);
            }
            catch (Exception e) when (e is InvalidOperationException or SmtpException)
            {
                await audit.WriteAsync("alert.settings.test", "alert_settings", site.Id, $"to {to}: not sent");
                return BackToSettings($"The test email to {to} was not sent. {e.Message}");
            }

            await audit.WriteAsync("alert.settings.test", "alert_settings", site.Id, $"to {to}: sent");
            return BackToSettings($"Test email sent to {to}. If it does not arrive, look in its spam folder.");
        });

        alerts.MapPost("/events/{id:guid}/ack", async (Guid id, TalkWatchDbContext db, AlertService service, Audit audit, HttpContext http, CancellationToken cancellationToken) =>
        {
            // Acknowledging runs with the system's view, so first: is this alert one the person may see?
            if (!await db.AlertEvents.AnyAsync(e => e.Id == id, cancellationToken)
                || !await service.AcknowledgeAsync(id, http.User.Identity?.Name ?? "an admin", cancellationToken))
            {
                return Results.NotFound();
            }

            await audit.WriteAsync("alert.ack", "alert_event", id, null);
            return Back("Acknowledged.");
        });
    }

    /// <summary>
    /// Acknowledge and snooze from a link in a notification, without signing in: the signed token in the path is the
    /// permission. POST only, so a mail scanner fetching links acknowledges nothing; the page at /a/{token} has the
    /// buttons, and ntfy's action button posts directly.
    /// </summary>
    public static void MapAlertLinks(this IEndpointRouteBuilder app)
    {
        // A 404 stays a 404, rather than being re-run through the not-found page, whose antiforgery check turns it into a 400.
        var links = app.MapGroup("/a").DisableAntiforgery().WithMetadata(new SkipStatusCodePagesAttribute());

        links.MapPost("/{token}/ack", async (string token, AlertLinks links, AlertService service, Audit audit, CancellationToken cancellationToken) =>
        {
            if (links.Read(token) is not { } id || !await service.AcknowledgeAsync(id, AlertService.ByLink, cancellationToken))
            {
                return Results.NotFound();
            }

            await audit.WriteAsync("alert.ack", "alert_event", id, "link");
            return Results.Redirect($"/a/{token}?done=ack");
        });

        links.MapPost("/{token}/snooze", async (string token, int? minutes, AlertLinks links, AlertService service, Audit audit, CancellationToken cancellationToken) =>
        {
            var duration = TimeSpan.FromMinutes(Math.Clamp(minutes ?? 60, 5, 24 * 60));
            if (links.Read(token) is not { } id || !await service.SnoozeAsync(id, duration, cancellationToken))
            {
                return Results.NotFound();
            }

            await audit.WriteAsync("alert.snooze", "alert_event", id, $"{duration.TotalMinutes:0} minutes");
            return Results.Redirect($"/a/{token}?done=snooze");
        });
    }

    private static bool TryWindow(List<DayOfWeek>? days, string? start, string? end, out int? mask, out TimeOnly? from, out TimeOnly? to)
    {
        (mask, from, to) = (null, null, null);
        if (days is not { Count: > 0 })
        {
            return true;
        }

        if (!TimeOnly.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, out var s)
            || !TimeOnly.TryParse(end, System.Globalization.CultureInfo.InvariantCulture, out var e) || s == e)
        {
            return false;
        }

        (mask, from, to) = (TimeWindow.ToMask(days), s, e);
        return true;
    }

    /// <summary>What is wrong with a new channel's address and secret for its kind, or null when nothing is.</summary>
    private static string? TargetProblem(ChannelForm form, TelegramOptions telegram)
    {
        var target = form.Target?.Trim();
        switch (form.Kind)
        {
            case ChannelKind.Email:
                return MailAddress.TryCreate(target, out _) ? null : "Give an email address.";

            case ChannelKind.Browser:
                return string.IsNullOrEmpty(form.Owner) ? "A browser channel is one person's: choose who it is for. People can also turn it on from their account page." : null;

            case ChannelKind.Telegram:
                if (string.IsNullOrEmpty(target) ? string.IsNullOrWhiteSpace(telegram.ChatId) : !TelegramChat().IsMatch(target))
                {
                    return "Give the Telegram chat id (a number such as -1001234567890, or @channelname), or set Telegram__ChatId.";
                }

                if (string.IsNullOrEmpty(form.Secret) ? string.IsNullOrWhiteSpace(telegram.BotToken) : !TelegramToken().IsMatch(form.Secret))
                {
                    return "Give the bot token from @BotFather (digits, a colon, then letters), or set Telegram__BotToken.";
                }

                return null;

            default:
                return IsWebAddress(target) ? null : "Give an http or https address.";
        }
    }

    [GeneratedRegex(@"^(-?\d+|@[A-Za-z0-9_]{5,})$")]
    private static partial Regex TelegramChat();

    [GeneratedRegex(@"^\d+:[A-Za-z0-9_-]{20,}$")]
    private static partial Regex TelegramToken();

    /// <summary>May manage every alert, not just their own.</summary>
    private static bool IsAdmin(HttpContext http) => http.User.Can(Permission.ManageAlerts);

    private static Guid? UserId(HttpContext http) =>
        Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    private static bool IsWebAddress(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static IResult Back(string message) => Results.Redirect($"/admin/alerts?msg={Uri.EscapeDataString(message)}");

    // Back to the settings, with the answer shown beside them: the page opens at the top otherwise, and a refusal there
    // went unseen while the field showed the saved value again, as though the change had not been saved.
    private static IResult BackToSettings(string message) => Results.Redirect($"/admin/alerts?msg={Uri.EscapeDataString(message)}&at=settings#settings");

    /// <summary>
    /// A channel that was not added goes back to its own form, saying why there and keeping what was typed, so nothing
    /// has to be typed again; all but the secret, which never goes in an address.
    /// </summary>
    private static IResult Refused(string problem, ChannelForm form)
    {
        string E(string? s) => Uri.EscapeDataString(s ?? "");
        var days = string.Join(",", form.QuietDays ?? []);
        return Results.Redirect($"/admin/alerts?problem={E(problem)}&name={E(form.Name)}&kind={E(form.Kind.ToString())}&target={E(form.Target)}&owner={E(form.Owner)}"
            + $"&days={E(days)}&from={E(form.QuietStart)}&to={E(form.QuietEnd)}#add-channel");
    }

    // Flows have their own page, so a change to one goes back there.
    private static IResult FlowsBack(string message) => Results.Redirect($"/flows?msg={Uri.EscapeDataString(message)}");
}

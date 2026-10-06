using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Encrypts channel secrets (ntfy tokens, webhook signing secrets) with the app's data-protection keys.</summary>
public sealed class ChannelSecrets(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("TalkWatch.AlertChannelSecrets.v1");

    public string? Protect(string? secret) => string.IsNullOrEmpty(secret) ? null : _protector.Protect(secret);

    public string? Unprotect(string? protectedSecret) => protectedSecret is null ? null : _protector.Unprotect(protectedSecret);
}

/// <summary>
/// Sends queued alerts, retrying failures with back-off (1, 5, 15 minutes, 1 and 6 hours, then every 6 hours) and
/// giving up after <see cref="MaxAttempts"/>. A channel that is down delays its own alerts, never anyone else's.
/// Alerts due in a channel's quiet hours wait for them to end; alerts acknowledged before they go are not sent.
/// </summary>
public sealed partial class AlertDispatcher(
    IServiceScopeFactory scopes, IHttpClientFactory http, ChannelSecrets secrets, AlertLinks links, AlertService alerts,
    IOptions<SiteOptions> site, AlertSettingsStore settings, BrowserAlerts browser, WebPushSender push, TimeProvider clock, ILogger<AlertDispatcher> logger,
    IOptions<DemoOptions> demo, MailSender mailer)
    : BackgroundService
{
    public const string HttpClientName = "alerts";

    /// <summary>For a channel someone owns who doesn't manage every alert: public addresses only (<see cref="OutboundGuard"/>).</summary>
    public const string PublicHttpClientName = "alerts-public";

    private const int MaxChatText = 3500, MaxEmailText = 20000;

    /// <summary>A voicemail as an email carries it.</summary>
    public sealed record VoicemailFile(byte[] Bytes, string Name, string ContentType);

    /// <summary>What a channel gets about the call besides the alert: none of it unless its owner may read or hear it.</summary>
    public sealed record Extras(string? Summary, string? Transcript, VoicemailFile? Voicemail, Uri? VoicemailPage)
    {
        public static readonly Extras None = new(null, null, null, null);

        /// <summary>The alert's message with the summary, what was said and where to play the voicemail, trimmed to fit.</summary>
        public string AppendTo(string message, int limit)
        {
            var text = new StringBuilder(message);
            if (Summary is { Length: > 0 } summary)
            {
                text.Append("\n\nSummary: ").Append(summary);
            }

            if (Transcript is { Length: > 0 } transcript)
            {
                text.Append("\n\nWhat was said:\n").Append(transcript);
            }

            if (VoicemailPage is { } voicemail)
            {
                text.Append("\n\nPlay the voicemail: ").Append(voicemail);
            }

            return Trim(text.ToString(), limit);
        }
    }

    private static string Trim(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";

    // The largest voicemail an email takes as an attachment; a longer one is a link instead.
    private const long MaxAttachment = 10 * 1024 * 1024;

    /// <summary>
    /// Whether a channel may reach the LAN's private addresses: a site channel, or one owned by someone who manages every
    /// alert, may, as a self-hosted ntfy or Home Assistant needs. Anyone else's reaches public addresses only, so a
    /// Manager's channel is no way into the network TalkWatch sits on.
    /// </summary>
    private async Task<bool> MayReachTheLanAsync(AlertChannel channel, CancellationToken cancellationToken)
    {
        if (channel.OwnerUserId is not { } owner)
        {
            return true;
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var permissions = RolePermissions.Of(await (
            from ur in db.UserRoles
            join rc in db.RoleClaims on ur.RoleId equals rc.RoleId
            where ur.UserId == owner
            select new System.Security.Claims.Claim(rc.ClaimType!, rc.ClaimValue!)).ToListAsync(cancellationToken));
        return permissions.HasFlag(Permission.ManageAlerts);
    }

    /// <summary>
    /// The summary, what was said and the voicemail, as the channel's owner may read and hear them: through their own
    /// access, the same query filters as every page, or the whole site's for a site channel. Only what exists by now.
    /// </summary>
    private async Task<Extras> ExtrasAsync(AlertChannel channel, CallRow call, NotifyIncludes include, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<AccessScopeHolder>();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        holder.UseSystemScope();
        if (channel.OwnerUserId is { } owner)
        {
            var permissions = RolePermissions.Of(await (
                from ur in db.UserRoles
                join rc in db.RoleClaims on ur.RoleId equals rc.RoleId
                where ur.UserId == owner
                select new System.Security.Claims.Claim(rc.ClaimType!, rc.ClaimValue!)).ToListAsync(cancellationToken));
            holder.UseScope(AccessScope.ForUser(channel.SiteId, owner, permissions));
        }

        string? summary = null, transcript = null;
        if ((include & (NotifyIncludes.Summary | NotifyIncludes.Transcript)) != NotifyIncludes.None
            && await db.CallTranscripts.AsNoTracking().Where(t => t.CallId == call.Id).Select(t => new { t.Summary, t.Text }).FirstOrDefaultAsync(cancellationToken) is { } said)
        {
            summary = include.HasFlag(NotifyIncludes.Summary) ? said.Summary : null;
            transcript = include.HasFlag(NotifyIncludes.Transcript) ? said.Text : null;
        }

        VoicemailFile? file = null;
        Uri? playAt = null;
        if (include.HasFlag(NotifyIncludes.Voicemail)
            && await db.AudioFiles.AsNoTracking().FirstOrDefaultAsync(a => a.CallId == call.Id && a.Kind == AudioKind.Voicemail && a.State == AudioState.Copied, cancellationToken) is { RelativePath: { } path } audio)
        {
            playAt = site.Value.PublicUrl is { } url ? new Uri(url, $"/calls/{call.TalkUuid}") : null;
            if (audio.SizeBytes is > 0 and <= MaxAttachment)
            {
                await using var read = scope.ServiceProvider.GetRequiredService<AudioStore>().OpenRead(path);
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy, cancellationToken);
                file = new VoicemailFile(copy.ToArray(), $"voicemail-{call.Time:yyyy-MM-dd-HHmm}{Path.GetExtension(path)}", audio.ContentType ?? "audio/mpeg");
            }
        }

        return new Extras(summary, transcript, file, playAt);
    }

    /// <summary>Why word of a call put through outside was not sent: it came in the channel's quiet hours.</summary>
    public const string QuietDropped = "Not sent: it came in the channel's quiet hours, and would be stale by the time they end.";

    /// <summary>Why a demo did not send something: it sends nothing out of the browser.</summary>
    public const string InBrowserOnly = "The demo sends alerts only to the browser.";

    // Also for a channel's test: nothing leaves a demo, whichever way a send is asked for.
    private void OnlyInTheBrowserInADemo(AlertChannel channel)
    {
        if (demo.Value.Enabled && channel.Kind != ChannelKind.Browser)
        {
            throw new InvalidOperationException(InBrowserOnly);
        }
    }
    public const int MaxAttempts = 8;

    private static readonly TimeSpan[] BackOff =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim _gate = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        do
        {
            // A failure is logged and tried again next round: a background service that throws stops the whole app, and
            // the database being out of reach for a minute did exactly that.
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception e)
#pragma warning restore CA1031
            {
                LogRoundFailed(logger, e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Moves flows on to their next stage where it is due, then sends everything due now. Returns how many were sent.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await alerts.AdvanceDueAsync(cancellationToken);

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var now = clock.GetUtcNow();
            var due = await db.AlertDeliveries.Where(d => d.State == DeliveryState.Pending && d.NextAttemptAt <= now)
                .OrderBy(d => d.NextAttemptAt).Take(20).ToListAsync(cancellationToken);

            var sent = 0;
            foreach (var delivery in due)
            {
                // Sent, or cancelled, already: as part of a bundle handled earlier in this round.
                if (delivery.State != DeliveryState.Pending)
                {
                    continue;
                }

                var alert = await db.AlertEvents.SingleAsync(e => e.Id == delivery.EventId, cancellationToken);
                var channel = await db.AlertChannels.SingleAsync(c => c.Id == delivery.ChannelId, cancellationToken);
                if (alert.AcknowledgedAt is not null && !delivery.Acknowledgement)
                {
                    delivery.State = DeliveryState.Cancelled;
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // A demo sends nothing out of the browser: anything else is cancelled, saying why, not retried.
                if (demo.Value.Enabled && channel.Kind != ChannelKind.Browser)
                {
                    (delivery.State, delivery.LastError) = (DeliveryState.Cancelled, InBrowserOnly);
                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                // A call put through to someone's phone outside is urgent enough, when the step says so, to wake them.
                if (QuietUntil(channel, now) is { } quietEnds && !(delivery.Outside && delivery.Urgent))
                {
                    // An alert waits for the quiet hours to end; word that one is in hand would be stale by then, and so
                    // would word of a call ringing a phone outside.
                    if (delivery.Acknowledgement)
                    {
                        delivery.State = DeliveryState.Cancelled;
                    }
                    else if (delivery.Outside)
                    {
                        (delivery.State, delivery.LastError) = (DeliveryState.Cancelled, QuietDropped);
                    }
                    else
                    {
                        delivery.NextAttemptAt = quietEnds;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    continue;
                }

                if (delivery.BundleKey is { } key && await BundleAsync(db, delivery, key, channel, now, cancellationToken) is { } bundled)
                {
                    sent += bundled;
                    continue;
                }

                var call = alert.CallId is { } callId ? await db.Calls.AsNoTracking().SingleOrDefaultAsync(c => c.Id == callId, cancellationToken) : null;
                var flowName = await db.AlertFlows.Where(f => f.Id == delivery.FlowId).Select(f => f.Name).SingleOrDefaultAsync(cancellationToken);
                delivery.Attempts++;
                try
                {
                    await SendAsync(channel, alert, call, delivery.Id, delivery.Stage, delivery.Urgent, cancellationToken, delivery.Acknowledgement, (NotifyIncludes)delivery.Include, flowName,
                        delivery.CallerOnly);
                    delivery.State = DeliveryState.Sent;
                    Telemetry.AlertDeliveries.Add(1, new("channel", channel.Kind.ToString()), new("result", "sent"));
                    delivery.SentAt = clock.GetUtcNow();
                    delivery.LastError = null;
                    sent++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // One channel failing must not stop the rest; the error is kept on the delivery.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    delivery.LastError = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                    if (delivery.Attempts >= MaxAttempts)
                    {
                        delivery.State = DeliveryState.Dead;
                        Telemetry.AlertDeliveries.Add(1, new("channel", channel.Kind.ToString()), new("result", "gave_up"));
                        LogGaveUp(logger, channel.Name, alert.Title, e);
                    }
                    else
                    {
                        delivery.NextAttemptAt = clock.GetUtcNow() + BackOff[Math.Min(delivery.Attempts - 1, BackOff.Length - 1)];
                        Telemetry.AlertDeliveries.Add(1, new("channel", channel.Kind.ToString()), new("result", "retrying"));
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Who or what acknowledged an alert and when, in words: a link in a notification does not say whose phone it was on.</summary>
    private string Acknowledged(AlertEvent alert)
    {
        var at = alert.AcknowledgedAt is { } when ? $" at {TimeZoneInfo.ConvertTime(when, alerts.Zone):HH:mm}" : "";
        return $"Acknowledged {AlertService.AcknowledgedHow(alert.AcknowledgedBy ?? AlertService.ByLink)}{at}.";
    }

    /// <summary>When the channel's quiet hours in force now end, or null when it is not quiet now.</summary>
    /// <summary>
    /// Sends a bundle: every delivery due now to this channel with this key, as one message. Alerts acknowledged since
    /// they were gathered are dropped. Returns how many were sent, or null when only this one is left, to send as usual.
    /// </summary>
    private async Task<int?> BundleAsync(TalkWatchDbContext db, AlertDelivery first, string key, AlertChannel channel, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var deliveries = await db.AlertDeliveries.Where(d => d.State == DeliveryState.Pending && d.ChannelId == channel.Id && d.BundleKey == key && d.NextAttemptAt <= now)
            .OrderBy(d => d.CreatedAt).ToListAsync(cancellationToken);
        var ids = deliveries.Select(d => d.EventId).ToList();
        var events = await db.AlertEvents.Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        foreach (var gone in deliveries.Where(d => events[d.EventId].AcknowledgedAt is not null))
        {
            gone.State = DeliveryState.Cancelled;
        }

        var live = deliveries.Where(d => d.State == DeliveryState.Pending).ToList();
        if (live.Count <= 1)
        {
            await db.SaveChangesAsync(cancellationToken);
            return live.Count == 1 && live[0].Id == first.Id ? null : 0;
        }

        var callIds = live.Select(d => events[d.EventId].CallId).OfType<Guid>().ToList();
        var calls = await db.Calls.AsNoTracking().Where(c => callIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var items = live.Select(d => (Delivery: d, Alert: events[d.EventId], Call: events[d.EventId].CallId is { } c ? calls.GetValueOrDefault(c) : null)).ToList();
        foreach (var d in live)
        {
            d.Attempts++;
        }

        try
        {
            await SendBundleAsync(channel, items, cancellationToken);
            foreach (var d in live)
            {
                (d.State, d.SentAt, d.LastError) = (DeliveryState.Sent, clock.GetUtcNow(), null);
            }

            Telemetry.AlertDeliveries.Add(live.Count, new("channel", channel.Kind.ToString()), new("result", "sent"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // As for one delivery: the error is kept, and the bundle tried again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            foreach (var d in live)
            {
                d.LastError = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                if (d.Attempts >= MaxAttempts)
                {
                    d.State = DeliveryState.Dead;
                }
                else
                {
                    d.NextAttemptAt = clock.GetUtcNow() + BackOff[Math.Min(d.Attempts - 1, BackOff.Length - 1)];
                }
            }

            if (live.Any(d => d.State == DeliveryState.Dead))
            {
                LogGaveUp(logger, channel.Name, $"a bundle of {live.Count} alerts", e);
            }

            await db.SaveChangesAsync(cancellationToken);
            return 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        return live.Count;
    }

    /// <summary>
    /// Several alerts as one message: "3 alerts", then a line for each. A webhook gets them as a list, each as its own
    /// alert would be, under the event "Bundle". Acknowledging is done on each alert's page, linked from the list.
    /// </summary>
    private async Task SendBundleAsync(AlertChannel channel, List<(AlertDelivery Delivery, AlertEvent Alert, CallRow? Call)> items, CancellationToken cancellationToken)
    {
        if (!channel.Enabled)
        {
            throw new InvalidOperationException("The channel is turned off.");
        }

        OnlyInTheBrowserInADemo(channel);
        var urgent = items.Any(i => i.Delivery.Urgent);
        var title = items.Select(i => i.Alert.Title).Distinct().Count() == 1 ? $"{items.Count} × {items[0].Alert.Title}" : $"{items.Count} alerts";
        var message = string.Join("\n", items.Select(i => $"• {i.Alert.Title}: {i.Alert.Message}"));
        var secret = secrets.Unprotect(channel.ProtectedSecret);

        switch (channel.Kind)
        {
            case ChannelKind.Email:
                await EmailAsync(channel.Target, title, message, null, cancellationToken);
                return;
            case ChannelKind.Browser:
                var owner = channel.OwnerUserId ?? throw new InvalidOperationException("A browser channel belongs to one person.");
                browser.Publish(owner);
                if (!demo.Value.Enabled)
                {
                    await push.SendAsync(owner, title, message, null, urgent, cancellationToken);
                }

                return;
            case ChannelKind.Telegram:
                await TelegramAsync(channel, secret, title, message, null, cancellationToken);
                return;
        }

        var client = http.CreateClient(await MayReachTheLanAsync(channel, cancellationToken) ? HttpClientName : PublicHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, channel.Target);
        request.Options.Set(Telemetry.NotTraced, true);
        if (channel.Kind == ChannelKind.Ntfy)
        {
            request.Content = new StringContent(message, Encoding.UTF8, "text/plain");
            request.Headers.Add("Title", NtfyText.Header(title));
            request.Headers.Add("Tags", "bell");
            request.Headers.Add("Priority", urgent ? "high" : "default");
            if (site.Value.PublicUrl is { } url)
            {
                request.Headers.Add("Click", new Uri(url, "/alerts/inbox").ToString());
            }

            if (secret is not null)
            {
                // A channel saved before tokens needed https: its token is still not sent in clear.
                if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
                {
                    throw new InvalidOperationException("An ntfy token is sent only over https: give the topic's https address.");
                }

                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }
        }
        else
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "Bundle",
                title,
                urgent,
                alerts = items.Select(i =>
                {
                    var page = links.PageFor(i.Alert.Id);
                    return new
                    {
                        id = i.Alert.Id,
                        type = i.Alert.Type.ToString(),
                        at = i.Alert.At,
                        title = i.Alert.Title,
                        message = i.Alert.Message,
                        stage = i.Delivery.Stage,
                        urgent = i.Delivery.Urgent,
                        call = i.Call is null ? null : new { uuid = i.Call.TalkUuid, time = i.Call.Time, direction = i.Call.Direction, status = i.Call.Status, from = i.Call.FromE164 ?? i.Call.FromRaw, to = i.Call.ToE164 ?? i.Call.ToRaw },
                        userUuid = i.Alert.UserUuid,
                        links = page is null ? null : new { page, acknowledge = $"{page}/ack", snooze = $"{page}/snooze?minutes=60" },
                    };
                }),
            }, Json);
            request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            request.Headers.Add("X-TalkWatch-Event", "Bundle");
            request.Headers.Add("X-TalkWatch-Delivery", items[0].Delivery.Id.ToString());
            if (secret is not null)
            {
                request.Headers.Add("X-TalkWatch-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
            }
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{channel.Kind} answered HTTP {(int)response.StatusCode}.");
        }
    }

    // What TalkWatch calls the number a call rang ("Main line"), or null when it has no name for it.
    private async Task<string?> LineNameAsync(string? did, CancellationToken cancellationToken)
    {
        if (did is null)
        {
            return null;
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var name = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().Lines
            .Where(l => l.Kind == LineKind.Did && l.Key == did).Select(l => l.Name).FirstOrDefaultAsync(cancellationToken);
        return name is { Length: > 0 } && name != did ? name : null;
    }

    private DateTimeOffset? QuietUntil(AlertChannel channel, DateTimeOffset now)
    {
        if (channel.QuietDays is not { } days || channel.QuietStart is not { } start || channel.QuietEnd is not { } end)
        {
            return null;
        }

        var quiet = new TimeWindow(TimeWindow.FromMask(days), start, end, Outside: false);
        if (!quiet.Matches(now, alerts.Zone))
        {
            return null;
        }

        // A minute at a time: quiet hours are whole minutes, and no window is longer than a week.
        var at = now.AddTicks(-(now.Ticks % TimeSpan.TicksPerMinute)).AddMinutes(1);
        for (var i = 0; i < 7 * 24 * 60 && quiet.Matches(at, alerts.Zone); i++)
        {
            at = at.AddMinutes(1);
        }

        return at;
    }

    /// <summary>
    /// Sends one alert to one channel now. Throws on failure. Also used for a channel's test button. A stage after the
    /// first says nobody has picked it up yet; urgent is sent at high priority where the channel has one. An
    /// acknowledgement says who has it in hand, quietly and with nothing left to press.
    /// </summary>
    /// <param name="callerOnly">
    /// For someone who may not see the call but was put it through outside: who is calling and on which line, with nothing
    /// about the call besides and no link to it or the alert, whatever else is asked for.
    /// </param>
    public async Task SendAsync(AlertChannel channel, AlertEvent alert, CallRow? call, Guid deliveryId, int stage, bool urgent, CancellationToken cancellationToken,
        bool acknowledgement = false, NotifyIncludes include = NotifyIncludes.None, string? flow = null, bool callerOnly = false)
    {
        if (!channel.Enabled)
        {
            throw new InvalidOperationException("The channel is turned off.");
        }

        var escalation = stage > 0 && !acknowledgement;
        var title = acknowledgement ? $"Acknowledged: {alert.Title}" : escalation ? $"Still not picked up: {alert.Title}" : alert.Title;
        var message = acknowledgement ? $"{alert.Message}. {Acknowledged(alert)}" : alert.Message;
        var page = alert.Key == "test" || acknowledgement || callerOnly ? null : links.PageFor(alert.Id);
        var secret = secrets.Unprotect(channel.ProtectedSecret);
        OnlyInTheBrowserInADemo(channel);
        var extras = include != NotifyIncludes.None && call is not null && !acknowledgement && !callerOnly
            ? await ExtrasAsync(channel, call, include, cancellationToken)
            : Extras.None;

        if (channel.Kind == ChannelKind.Email)
        {
            await EmailAsync(channel.Target, title, extras.AppendTo(message, MaxEmailText), page, cancellationToken, extras.Voicemail);
            return;
        }

        // The chat and push channels carry it as text, trimmed to what each takes, and the voicemail as a link to play it.
        // ntfy lays the call out line by line first: who and what, the number and the line rung, when, the flow.
        if (channel.Kind == ChannelKind.Ntfy)
        {
            var facts = call is null ? null : new NtfyText.CallFacts(call.CallerName, call.FromE164, await LineNameAsync(call.ToE164, cancellationToken), call.Time, call.DurationSeconds);
            (title, var body) = NtfyText.Compose(alert.Type, alert.Title, alert.Message, facts, flow, alerts.Zone, escalation, acknowledgement, acknowledgement ? Acknowledged(alert) : null);
            message = extras.AppendTo(body, MaxChatText);
        }
        else if (channel.Kind == ChannelKind.Telegram)
        {
            message = extras.AppendTo(message, MaxChatText);
        }

        if (channel.Kind == ChannelKind.Browser)
        {
            // In the bell as soon as it is sent; open pages are told, and allowed browsers get a desktop notification.
            var owner = channel.OwnerUserId ?? throw new InvalidOperationException("A browser channel belongs to one person.");
            browser.Publish(owner);
            // A desktop notification goes through the browser's push service, out of TalkWatch: not in a demo.
            if (!demo.Value.Enabled)
            {
                await push.SendAsync(owner, title, extras.Summary is { } said ? $"{message}. {Trim(said, 200)}" : message, page, urgent || alert.Type is AlertEventType.Drift or AlertEventType.AccountProblem, cancellationToken);
            }

            return;
        }

        if (channel.Kind == ChannelKind.Telegram)
        {
            await TelegramAsync(channel, secret, title, message, page, cancellationToken);
            return;
        }

        var client = http.CreateClient(await MayReachTheLanAsync(channel, cancellationToken) ? HttpClientName : PublicHttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, channel.Target);
        request.Options.Set(Telemetry.NotTraced, true);

        switch (channel.Kind)
        {
            case ChannelKind.Ntfy:
                request.Content = new StringContent(message, Encoding.UTF8, "text/plain");
                request.Headers.Add("Title", NtfyText.Header(title));
                request.Headers.Add("Tags", acknowledgement ? "white_check_mark" : alert.Type switch
                {
                    AlertEventType.MissedCall => "telephone_receiver,x",
                    AlertEventType.Voicemail or AlertEventType.VoicemailTranscribed => "mailbox_with_mail",
                    AlertEventType.HandsetOffline => "warning",
                    AlertEventType.HungUpAtSwitchboard => "telephone_receiver,wave",
                    AlertEventType.Drift => "rotating_light",
                    AlertEventType.NegativeCall => "disappointed",
                    AlertEventType.PoorQualityCall => "signal_strength",
                    AlertEventType.HandsetUnregistered or AlertEventType.HandsetUpdateAvailable => "telephone",
                    AlertEventType.AccountProblem or AlertEventType.SettingTurnedOff => "rotating_light",
                    _ => "telephone_receiver",
                });
                request.Headers.Add("Priority", acknowledgement ? "low" : urgent || alert.Type is AlertEventType.Drift or AlertEventType.AccountProblem ? "high" : "default");
                // A tap opens the call itself, for someone who may see it.
                if (site.Value.PublicUrl is { } url && !callerOnly)
                {
                    request.Headers.Add("Click", new Uri(url, call is null ? "/calls" : $"/calls/{Uri.EscapeDataString(call.TalkUuid)}").ToString());
                }

                // Buttons, three at most: acknowledge straight from the phone, ring the caller back from its dialler, or open
                // the alert to snooze it or see more. An acknowledgement has nothing left to press.
                var actions = new List<string>();
                if (page is not null)
                {
                    actions.Add($"http, Acknowledge, {page}/ack, method=POST, clear=true");
                }

                if (!acknowledgement && call?.FromE164 is { } callBack)
                {
                    actions.Add($"view, Call back, tel:{callBack}");
                }

                if (page is not null)
                {
                    actions.Add($"view, Open, {page}");
                }

                if (actions.Count > 0)
                {
                    request.Headers.Add("Actions", string.Join("; ", actions));
                }

                if (secret is not null)
                {
                    // A channel saved before tokens needed https: its token is still not sent in clear.
                    if (request.RequestUri?.Scheme != Uri.UriSchemeHttps)
                    {
                        throw new InvalidOperationException("An ntfy token is sent only over https: give the topic's https address.");
                    }

                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                }

                break;

            case ChannelKind.Webhook:
                var body = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    id = alert.Id,
                    type = alert.Type.ToString(),
                    at = alert.At,
                    title,
                    message,
                    escalation,
                    acknowledged = alert.AcknowledgedAt is { } ackAt ? new { at = ackAt, by = alert.AcknowledgedBy } : null,
                    stage,
                    urgent,
                    call = call is null ? null
                        : callerOnly ? new { uuid = (string?)null, time = call.Time, direction = call.Direction, status = (string?)null, from = call.FromE164 ?? call.FromRaw, to = call.ToE164 ?? call.ToRaw }
                        : new { uuid = (string?)call.TalkUuid, time = call.Time, direction = call.Direction, status = (string?)call.Status, from = call.FromE164 ?? call.FromRaw, to = call.ToE164 ?? call.ToRaw },
                    userUuid = alert.UserUuid,
                    links = page is null ? null : new { page, acknowledge = $"{page}/ack", snooze = $"{page}/snooze?minutes=60" },
                    summary = extras.Summary,
                    transcript = extras.Transcript,
                    voicemail = extras.VoicemailPage,
                }, Json);
                request.Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
                request.Headers.Add("X-TalkWatch-Event", acknowledgement ? "Acknowledged" : alert.Type.ToString());
                request.Headers.Add("X-TalkWatch-Delivery", deliveryId.ToString());
                if (secret is not null)
                {
                    // Over the exact bytes sent, so the receiver can verify the alert came from TalkWatch.
                    request.Headers.Add("X-TalkWatch-Signature", "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)));
                }

                break;
        }

        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"{channel.Kind} answered HTTP {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// Bot API sendMessage. The channel's own bot token and chat id win; the Telegram__ settings fill in whatever it
    /// leaves empty. Acknowledge is a button that opens the alert's page: a callback button would need TalkWatch to
    /// take updates from Telegram, which it does not.
    /// </summary>
    private async Task TelegramAsync(AlertChannel channel, string? channelToken, string title, string message, Uri? page, CancellationToken cancellationToken)
    {
        var defaults = await settings.TelegramAsync(cancellationToken);
        var token = channelToken ?? defaults.BotToken;
        var chat = string.IsNullOrWhiteSpace(channel.Target) ? defaults.ChatId : channel.Target;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(chat))
        {
            throw new InvalidOperationException("Telegram needs a bot token and a chat id: on the channel, in the alert settings, or in Telegram__BotToken and Telegram__ChatId.");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["chat_id"] = chat,
            ["text"] = $"<b>{WebUtility.HtmlEncode(title)}</b>\n{WebUtility.HtmlEncode(message)}",
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new { is_disabled = true },
            ["reply_markup"] = page is null ? null : new { inline_keyboard = new[] { new[] { new { text = "Acknowledge or snooze", url = page.ToString() } } } },
        }, Json);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.telegram.org/bot{token}/sendMessage")
        {
            Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        request.Options.Set(Telemetry.NotTraced, true);
        using var response = await http.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Telegram says why ("chat not found", "bot was blocked by the user"); the URL, which holds the token, is left out.
            string? description = null;
            try
            {
                using var reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                description = reply.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
            }
            catch (JsonException)
            {
            }

            throw new HttpRequestException($"Telegram answered HTTP {(int)response.StatusCode}{(description is null ? "" : $": {description}")}.");
        }
    }

    private async Task EmailAsync(string to, string subject, string message, Uri? page, CancellationToken cancellationToken, VoicemailFile? voicemail = null)
    {
        var options = await mailer.OptionsAsync(cancellationToken);

        var body = new StringBuilder(message).AppendLine().AppendLine();
        if (page is not null)
        {
            body.Append("Acknowledge or snooze: ").Append(page).AppendLine();
        }

        body.AppendLine("Sent by TalkWatch.");

        using var mail = new MailMessage(options.From, to, subject, body.ToString());
        if (voicemail is not null)
        {
            mail.Attachments.Add(new Attachment(new MemoryStream(voicemail.Bytes), voicemail.Name, voicemail.ContentType));
        }
        await mailer.SendAsync(options, mail, cancellationToken);
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up sending '{Title}' to {Channel} after the last retry.")]
    private static partial void LogGaveUp(ILogger logger, string channel, string title, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Moving flows on or sending alerts failed; trying again in five seconds.")]
    private static partial void LogRoundFailed(ILogger logger, Exception exception);
}

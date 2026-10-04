using System.Security.Cryptography;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Tells open TalkWatch pages a browser alert has arrived for someone, so their bell updates and pops up.</summary>
public sealed class BrowserAlerts
{
    public event Action<Guid>? Arrived;

    public void Publish(Guid userId) => Arrived?.Invoke(userId);

    /// <summary>The alerts sent to someone's browser channel, newest first: what their bell lists.</summary>
    public static IQueryable<BrowserAlert> InboxOf(TalkWatchDbContext db, Guid userId) =>
        from d in db.AlertDeliveries
        join c in db.AlertChannels on d.ChannelId equals c.Id
        join e in db.AlertEvents on d.EventId equals e.Id
        where c.Kind == ChannelKind.Browser && c.OwnerUserId == userId && d.State == DeliveryState.Sent && !d.Acknowledgement
        orderby d.CreatedAt descending
        select new BrowserAlert
        {
            DeliveryId = d.Id, EventId = e.Id, Title = e.Title, Message = e.Message, At = d.CreatedAt, SeenAt = d.SeenAt, AcknowledgedAt = e.AcknowledgedAt, Escalation = d.Stage > 0,
        };
}

/// <summary>One alert in someone's bell. Set by initialisers, not a constructor, so EF can filter and count on it.</summary>
public sealed class BrowserAlert
{
    public Guid DeliveryId { get; init; }
    public Guid EventId { get; init; }
    public required string Title { get; init; }
    public required string Message { get; init; }
    public DateTimeOffset At { get; init; }
    public DateTimeOffset? SeenAt { get; init; }
    public DateTimeOffset? AcknowledgedAt { get; init; }
    public bool Escalation { get; init; }
}

/// <summary>
/// Desktop notifications through the browsers' own push services (Web Push), signed with a key pair TalkWatch makes the
/// first time one is needed and keeps, since browsers that allowed notifications trust its public key. A browser whose
/// push service says it is gone (404 or 410) is forgotten.
/// </summary>
public sealed partial class WebPushSender(
    IServiceScopeFactory scopes, IHttpClientFactory http, ChannelSecrets secrets, IOptions<SiteOptions> site, ILogger<WebPushSender> logger) : IDisposable
{
    public const string HttpClientName = "webpush";

    private readonly SemaphoreSlim _keys = new(1, 1);
    private (string Public, string Private)? _cached;

    /// <summary>The public key browsers subscribe with, made and kept the first time it is asked for.</summary>
    public async Task<string> PublicKeyAsync(CancellationToken cancellationToken) => (await KeysAsync(cancellationToken)).Public;

    private async Task<(string Public, string Private)> KeysAsync(CancellationToken cancellationToken)
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        await _keys.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var settings = await db.AlertSettings.SingleOrDefaultAsync(cancellationToken);
            if (settings is null)
            {
                settings = new AlertSettings { SiteId = scope.ServiceProvider.GetRequiredService<CurrentSite>().Id };
                db.AlertSettings.Add(settings);
            }

            if (settings.VapidPublicKey is null || settings.VapidProtectedPrivateKey is null)
            {
                // A P-256 key pair, as Web Push wants: the public key as an uncompressed point, the private key as its scalar.
                using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
                var parameters = key.ExportParameters(includePrivateParameters: true);
                settings.VapidPublicKey = Base64Url([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);
                settings.VapidProtectedPrivateKey = secrets.Protect(Base64Url(parameters.D!));
                settings.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }

            _cached = (settings.VapidPublicKey, secrets.Unprotect(settings.VapidProtectedPrivateKey)!);
            return _cached.Value;
        }
        finally
        {
            _keys.Release();
        }
    }

    /// <summary>A notification to every browser someone allowed. Best effort: the alert is in their bell either way.</summary>
    public async Task SendAsync(Guid userId, string title, string body, Uri? link, bool urgent, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var browsers = await db.PushSubscriptions.Where(p => p.UserId == userId).ToListAsync(cancellationToken);
        if (browsers.Count == 0)
        {
            return;
        }

        var (publicKey, privateKey) = await KeysAsync(cancellationToken);
        var client = new PushServiceClient(http.CreateClient(HttpClientName));
        var vapid = new VapidAuthentication(publicKey, privateKey)
        {
            Subject = site.Value.PublicUrl?.ToString().TrimEnd('/') is { } url && url.StartsWith("https://", StringComparison.Ordinal) ? url : "mailto:talkwatch@localhost",
        };
        var payload = JsonSerializer.Serialize(new { title, body, url = link?.ToString() ?? "/alerts/inbox", tag = "talkwatch" });
        foreach (var browser in browsers)
        {
            var subscription = new PushSubscription { Endpoint = browser.Endpoint };
            subscription.SetKey(PushEncryptionKeyName.P256DH, browser.P256dh);
            subscription.SetKey(PushEncryptionKeyName.Auth, browser.Auth);
            try
            {
                await client.RequestPushMessageDeliveryAsync(subscription,
                    new PushMessage(payload) { Urgency = urgent ? PushMessageUrgency.High : PushMessageUrgency.Normal, TimeToLive = 24 * 60 * 60 },
                    vapid, VapidAuthenticationScheme.Vapid, cancellationToken);
            }
            catch (PushServiceClientException e) when (e.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                db.PushSubscriptions.Remove(browser);
            }
            catch (Exception e) when (e is PushServiceClientException or HttpRequestException or TaskCanceledException)
            {
                LogFailed(logger, e);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public void Dispose() => _keys.Dispose();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [LoggerMessage(Level = LogLevel.Warning, Message = "A desktop notification could not be delivered; the alert is in the bell all the same.")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}

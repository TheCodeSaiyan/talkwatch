using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace TalkWatch.Web.Services;

/// <summary>
/// Links in notifications that acknowledge or snooze an alert without signing in. The token is the alert's id,
/// signed and time-limited with the app's data-protection keys, so nothing is stored and a link cannot be forged or
/// pointed at another alert. Anyone holding the link can act on that one alert for <see cref="Lifetime"/>, which is
/// the point of it: it goes to the people the alert went to.
/// </summary>
public sealed class AlertLinks(IDataProtectionProvider provider, IOptions<SiteOptions> site)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);

    private readonly ITimeLimitedDataProtector _protector = provider.CreateProtector("TalkWatch.AlertLinks.v1").ToTimeLimitedDataProtector();

    public string Token(Guid eventId) => _protector.Protect(eventId.ToString("N"), Lifetime);

    /// <summary>The alert a token is for, or null when it is forged, damaged or expired.</summary>
    public Guid? Read(string token)
    {
        try
        {
            return Guid.TryParseExact(_protector.Unprotect(token), "N", out var id) ? id : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>The page that offers acknowledge and snooze, or null when Site__PublicUrl is not set.</summary>
    public Uri? PageFor(Guid eventId) => site.Value.PublicUrl is { } root ? new Uri(root, $"/a/{Token(eventId)}") : null;
}

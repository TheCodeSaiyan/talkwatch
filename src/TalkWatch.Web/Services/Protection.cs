using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace TalkWatch.Web.Services;

/// <summary>
/// What protects every response and the sign-in form: security headers with a per-request script nonce, the real
/// client address from a trusted reverse proxy, and a limit on sign-in attempts per address.
/// </summary>
public static class Protection
{
    public const string SignInLimit = "sign-in";

    /// <summary>The key under which each request's script nonce is kept in HttpContext.Items.</summary>
    public const string NonceKey = "csp-nonce";

    /// <summary>
    /// Proxy__TrustedNetworks: the reverse proxy's networks, comma-separated CIDRs such as 192.168.90.0/24. Forwarded
    /// headers are believed only from these, since from anyone else they would let a client pick its own address.
    /// Empty by default, which leaves only loopback trusted. SignIn__AttemptsPerMinute: attempts allowed per client
    /// address, 10 by default, on top of the per-account lockout.
    /// </summary>
    public static void AddProtection(this WebApplicationBuilder builder)
    {
        var trusted = (builder.Configuration.GetSection(ProxyOptions.Section).Get<ProxyOptions>()?.TrustedNetworks ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(cidr => System.Net.IPNetwork.TryParse(cidr, out var network)
                ? network
                : throw new InvalidOperationException($"Proxy__TrustedNetworks: '{cidr}' is not a network such as 192.168.90.0/24."))
            .ToList();
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            trusted.ForEach(o.KnownIPNetworks.Add);
        });

        var perMinute = (builder.Configuration.GetSection(SignInLimitOptions.Section).Get<SignInLimitOptions>() ?? new()).AttemptsPerMinute;
        builder.Services.AddRateLimiter(o =>
        {
            o.AddPolicy(SignInLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.OnRejected = (rejected, _) =>
            {
                rejected.HttpContext.Response.Redirect("/signin?limited=1");
                return ValueTask.CompletedTask;
            };
        });
    }

    /// <summary>First in the pipeline: every response, errors and static files included, carries the headers.</summary>
    public static void UseProtection(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.Use(async (context, next) =>
        {
            // Hex rather than base64: base64's '+' is written as &#x2B; in the attribute, which is correct but needless.
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;

                // Scripts only from here, plus the one import map Blazor renders inline, which carries the nonce.
                // Inline styles are allowed: the dashboard's bars are sized with style attributes.
                headers.ContentSecurityPolicy =
                    $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
                    // blob: for media only: the player fetches a recording once, from here, and plays that download.
                    "media-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";
                headers.XContentTypeOptions = "nosniff";
                headers.XFrameOptions = "DENY";

                // Alert links carry their token in the path; no page here should hand its address to another site.
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
                return Task.CompletedTask;
            });
            await next();
        });
    }
}

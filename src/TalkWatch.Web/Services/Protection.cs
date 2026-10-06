using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
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

        // Reached over https, as the public address says: the sign-in cookies are sent over https alone, whatever scheme
        // TalkWatch sees behind its proxy. Without this, a proxy whose forwarded headers aren't trusted, such as Railway's,
        // left the sign-in cookie free to go over plain http, where anyone on the way could take it. (Not the antiforgery
        // cookie: it refuses to be made at all on a request that looks like http, which would break every form there.)
        if (ServedOverHttps(builder.Configuration))
        {
            builder.Services.PostConfigureAll<CookieAuthenticationOptions>(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
        }

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

    /// <summary>
    /// Refuses a form post without the page's antiforgery token, with 400. .NET checks the token itself only on an endpoint
    /// that reads a form, so one that takes nothing but its address (lock, delete, sweep, sign out) was protected by the
    /// SameSite=Strict cookie alone, which a site on a sibling subdomain gets past. JSON is left alone: no other site can
    /// send it without the browser asking first.
    /// </summary>
    public static TBuilder CheckFormToken<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (HttpMethods.IsPost(http.Request.Method) && !(http.Request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) ?? false)
                && !await http.RequestServices.GetRequiredService<IAntiforgery>().IsRequestValidAsync(http))
            {
                return Results.BadRequest();
            }

            return await next(context);
        });

    /// <summary>Whether people reach TalkWatch over https, as Site__PublicUrl says.</summary>
    private static bool ServedOverHttps(IConfiguration configuration) =>
        configuration.GetSection(SiteOptions.Section).Get<SiteOptions>()?.PublicUrl?.Scheme == Uri.UriSchemeHttps;

    /// <summary>First in the pipeline: every response, errors and static files included, carries the headers.</summary>
    public static void UseProtection(this WebApplication app)
    {
        var https = ServedOverHttps(app.Configuration);
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
                // A browser that has been here over https comes back over https, before any cookie can go in the clear.
                // Sent on every response, as the proxy in front may not say the request was https.
                if (https)
                {
                    headers.StrictTransportSecurity = "max-age=31536000";
                }
                return Task.CompletedTask;
            });
            await next();
        });
    }
}

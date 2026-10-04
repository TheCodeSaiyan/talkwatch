using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// What the demo guest may not change, though they are an admin: whatever would change the site for every visitor after
/// them (people, roles, group mappings, roles on numbers, retention, the mail and Telegram settings) or send anything out
/// of the browser (any alert channel but the browser's own, and desktop push). Flows, rules, reports, call-backs and the
/// rest are theirs to try. Checked on the server, for every change, so no page can forget it.
/// </summary>
public static class DemoGuard
{
    public const string Refusal = "That stays as it is in the demo, so it works for everyone who tries it after you.";

    /// <summary>Why this request is refused for the demo guest, or null when it may go ahead.</summary>
    public static async Task<string?> RefusalAsync(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method))
        {
            return null;
        }

        var path = request.Path.Value?.TrimEnd('/') ?? "";
        if (path.StartsWith("/admin/alerts", StringComparison.OrdinalIgnoreCase))
        {
            if (path.Equals("/admin/alerts/settings", StringComparison.OrdinalIgnoreCase))
            {
                return Refusal;
            }

            // Which outside contacts are answering lines changes how every call they take is read, for everyone after.
            if (path.Equals("/admin/alerts/answering-lines", StringComparison.OrdinalIgnoreCase) || path.Equals("/admin/alerts/answering-lines/reprocess", StringComparison.OrdinalIgnoreCase))
            {
                return Refusal;
            }

            if (path.Equals("/admin/alerts/settings/test", StringComparison.OrdinalIgnoreCase))
            {
                return "The demo sends no email: alerts arrive in the browser, and reports are kept here to read.";
            }

            // A new channel only to the browser: the bell, here in TalkWatch.
            if (path.Equals("/admin/alerts/channels", StringComparison.OrdinalIgnoreCase) && request.HasFormContentType)
            {
                var form = await request.ReadFormAsync();
                return Enum.TryParse<ChannelKind>(form["Kind"], ignoreCase: true, out var kind) && kind == ChannelKind.Browser
                    ? null
                    : "The demo sends alerts only to the browser: add a browser channel to see them arrive.";
            }

            return null;
        }

        if (path.StartsWith("/admin", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/numbers/people", StringComparison.OrdinalIgnoreCase))
        {
            return Refusal;
        }

        return path.Equals("/account/push", StringComparison.OrdinalIgnoreCase)
            ? "The demo shows alerts in the page, not as desktop notifications."
            : null;
    }

    /// <summary>Refuses what the guest may not change, back to the page they were on with the reason.</summary>
    public static IApplicationBuilder UseDemoGuard(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        var demo = context.RequestServices.GetRequiredService<IOptions<DemoOptions>>().Value;
        if (demo.Enabled && demo.IsGuest(context.User.Identity?.Name) && await RefusalAsync(context.Request) is { } why)
        {
            var back = Uri.TryCreate(context.Request.Headers.Referer.ToString(), UriKind.Absolute, out var referer) && referer.Host == context.Request.Host.Host
                ? referer.AbsolutePath
                : "/";
            if (context.Request.HasFormContentType)
            {
                context.Response.Redirect($"{back}?msg={Uri.EscapeDataString(why)}");
            }
            else
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync(why);
            }

            return;
        }

        await next();
    });
}

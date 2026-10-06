using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// The Console page's changes, for admins only: the console, the account TalkWatch signs in with, and the way to it.
/// Secrets saved here are encrypted and never shown again; every save is written to the audit log, naming which secrets
/// are saved, never their values.
/// </summary>
public static partial class ConsoleEndpoints
{
    // Plain classes with settable properties: form binding leaves a missing field at its default, and an unticked
    // checkbox sends nothing at all, so missing must mean no.
    public sealed class ConsoleForm
    {
        public string? ConsoleUrl { get; set; }
        public string? Username { get; set; }
        public string? Password { get; set; }
        public bool ClearPassword { get; set; }
        public string? CertificateSha256 { get; set; }

        /// <summary>Direct, WireGuard or Tailscale; empty for the Talk__Route setting.</summary>
        public string? Route { get; set; }

        /// <summary>A WireGuard .conf, pasted.</summary>
        public string? WireGuardConf { get; set; }

        /// <summary>The same, uploaded as the file the gateway gave out.</summary>
        public IFormFile? WireGuardFile { get; set; }

        // Or the same settings one by one.
        public string? WireGuardPrivateKey { get; set; }
        public string? WireGuardAddress { get; set; }
        public string? WireGuardPeerPublicKey { get; set; }
        public string? WireGuardEndpoint { get; set; }
        public string? WireGuardAllowedIps { get; set; }
        public string? WireGuardPresharedKey { get; set; }
        public string? WireGuardDns { get; set; }

        public bool ClearWireGuard { get; set; }
        public string? TailscaleAuthKey { get; set; }
        public bool ClearTailscaleAuthKey { get; set; }
        public string? TailscaleTags { get; set; }
    }

    public static void MapConsole(this IEndpointRouteBuilder app)
    {
        // A 404 stays a 404, rather than being re-run through the not-found page, whose antiforgery check turns it into a 400.
        var console = app.MapGroup("/admin/console").RequireAuthorization(Permissions.AdminPolicy).WithMetadata(new SkipStatusCodePagesAttribute());

        console.MapPost("/", async ([FromForm] ConsoleForm form, TalkWatchDbContext db, CurrentSite site, ChannelSecrets secrets, ConsoleConnection connection,
            IOptions<TalkOptions> options, Audit audit, TimeProvider clock, CancellationToken cancellationToken) =>
        {
            // https only: the console's password goes over TLS, to a console its certificate vouches for.
            Uri? newUrl = null;
            if (Text(form.ConsoleUrl) is { } url
                && (!Uri.TryCreate(url, UriKind.Absolute, out newUrl) || newUrl.Scheme != Uri.UriSchemeHttps || newUrl.AbsolutePath != "/"))
            {
                return Back("The console's address is just https:// and its host, such as https://192.168.1.1.");
            }

            // A password saved here, or in the settings, was given for the console it was given with. Moving to another
            // address needs it typed again, or it would go wherever an admin pointed it: a way to read a password never shown.
            var before = (await connection.GetAsync(cancellationToken)).Url;
            var after = newUrl ?? options.Value.ConsoleUrl;
            if (before is not null && after is not null && Uri.Compare(before, after, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0
                && string.IsNullOrEmpty(form.Password))
            {
                return Back("Changing the console's address needs its password again, so that a password never goes to an address it wasn't given for.");
            }

            if (Text(form.CertificateSha256) is { } pin && !Fingerprint().IsMatch(pin))
            {
                return Back("The certificate fingerprint is SHA-256 in hex: 64 characters, with or without colons.");
            }

            ConsoleRoute? route = null;
            if (Text(form.Route) is { } chosen)
            {
                if (!Enum.TryParse<ConsoleRoute>(chosen, out var r) || !Enum.IsDefined(r))
                {
                    return Back("Choose how TalkWatch reaches the console.");
                }

                route = r;
            }

            if (Text(form.TailscaleTags) is { } tags && tags.Split(',', StringSplitOptions.TrimEntries).Any(t => !Tag().IsMatch(t)))
            {
                return Back("Tailscale tags are names such as tag:talkwatch, comma-separated.");
            }

            // The file, pasted or uploaded, wins over fields typed in one by one; nothing given keeps what is saved.
            string? wireGuard = null;
            try
            {
                var conf = Text(form.WireGuardConf) ?? (form.WireGuardFile is { Length: > 0 and < 16_384 } file ? await ReadAsync(file, cancellationToken) : null);
                if (conf is not null)
                {
                    wireGuard = WireGuardConfig.Parse(conf).ToConf();
                }
                else if (Text(form.WireGuardPrivateKey) is not null || Text(form.WireGuardEndpoint) is not null)
                {
                    wireGuard = WireGuardConfig.FromFields(form.WireGuardPrivateKey, form.WireGuardAddress, form.WireGuardPeerPublicKey,
                        form.WireGuardEndpoint, form.WireGuardAllowedIps, form.WireGuardPresharedKey, form.WireGuardDns).ToConf();
                }
            }
            catch (FormatException e)
            {
                return Back("The WireGuard settings weren't saved: " + e.Message);
            }

            var saved = await db.ConsoleSettings.SingleOrDefaultAsync(cancellationToken);
            if (saved is null)
            {
                saved = new ConsoleSettings { SiteId = site.Id };
                db.ConsoleSettings.Add(saved);
            }

            saved.ConsoleUrl = Text(form.ConsoleUrl)?.TrimEnd('/');
            saved.Username = Text(form.Username);
            saved.CertificateSha256 = Text(form.CertificateSha256);
            saved.Route = route;
            saved.TailscaleTags = Text(form.TailscaleTags);
            saved.ProtectedPassword = form.ClearPassword ? null : !string.IsNullOrEmpty(form.Password) ? secrets.Protect(form.Password) : saved.ProtectedPassword;
            saved.ProtectedWireGuardConfig = form.ClearWireGuard ? null : wireGuard is not null ? secrets.Protect(wireGuard) : saved.ProtectedWireGuardConfig;
            saved.ProtectedTailscaleAuthKey = form.ClearTailscaleAuthKey ? null
                : Text(form.TailscaleAuthKey) is { } key ? secrets.Protect(key) : saved.ProtectedTailscaleAuthKey;
            saved.UpdatedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);

            await audit.WriteAsync("console.settings", "console_settings", site.Id, Clip(
                $"route {saved.Route?.ToString() ?? "as in the settings"}, console {saved.ConsoleUrl ?? "as in the settings"}; password {Said(saved.ProtectedPassword)}, "
                + $"wireguard {Said(saved.ProtectedWireGuardConfig)}, tailscale key {Said(saved.ProtectedTailscaleAuthKey)}"));

            var target = await connection.ReloadAsync(cancellationToken);
            return Back(target.Problem is { } problem ? "Saved, but: " + problem : "Saved. TalkWatch reconnects with these now.");
        });

        // Signs in afresh with what is saved, now, and reads which versions the console runs: the whole way there, tunnel,
        // certificate and account, checked at once.
        console.MapPost("/test", async (TalkSession session, ConsoleConnection connection, ConsoleTunnel tunnel, IngestionStatus status, CurrentSite site, Audit audit,
            CancellationToken cancellationToken) =>
        {
            var target = await connection.GetAsync(cancellationToken);
            if (target.Url is null)
            {
                return Back("Give the console's address first.");
            }

            if (target.Problem is { } problem)
            {
                return Back(problem);
            }

            session.Client.SessionEnded();
            status.BackOffUntil = null;
            string result;
            try
            {
                if (!await session.EnsureSignedInAsync(cancellationToken))
                {
                    result = $"The console refused to sign in: {status.LastError}";
                }
                else
                {
                    var versions = await session.Client.GetVersionsAsync(cancellationToken);
                    result = $"Connected{(target.Route == ConsoleRoute.Direct ? "" : $" through {target.Route}")}: Talk {versions.Talk ?? "?"} on UniFi OS {versions.UnifiOs ?? "?"}.";
                }
            }
            catch (Exception e) when (e is HttpRequestException or TalkApiException or IOException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                result = $"Couldn't reach the console{(tunnel.Status.State is TunnelState.Down or TunnelState.Starting ? $" ({tunnel.Status.Detail})" : "")}: {Innermost(e)}";
            }

            await audit.WriteAsync("console.test", "console_settings", site.Id, Clip(result));
            return Back(result);
        });
    }

    private static async Task<string> ReadAsync(IFormFile file, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(file.OpenReadStream());
        return await reader.ReadToEndAsync(cancellationToken);
    }

    // The certificate's own complaint, say, rather than the HttpRequestException wrapped round it.
    private static string Innermost(Exception e)
    {
        while (e.InnerException is not null)
        {
            e = e.InnerException;
        }

        return e.Message;
    }

    // The audit log keeps 500 characters of detail; an error from the console can run longer.
    private static string Clip(string detail) => detail.Length <= 500 ? detail : detail[..497] + "...";

    private static string Said(string? secret) => secret is null ? "not saved" : "saved";

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static IResult Back(string message) => Results.Redirect($"/admin/console?msg={Uri.EscapeDataString(message)}");

    [GeneratedRegex("^([0-9A-Fa-f]{2}:?){31}[0-9A-Fa-f]{2}$")]
    private static partial Regex Fingerprint();

    [GeneratedRegex("^(tag:)?[A-Za-z0-9][A-Za-z0-9-]*$")]
    private static partial Regex Tag();
}

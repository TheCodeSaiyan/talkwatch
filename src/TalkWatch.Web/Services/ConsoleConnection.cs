using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Talk;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// The console and the way to it, as TalkWatch uses them: what an admin saved on the Console page, field by field, over
/// the Talk__ settings.
/// </summary>
public sealed record ConsoleTarget
{
    public Uri? Url { get; init; }
    public string Username { get; init; } = "";
    public string Password { get; init; } = "";
    public string? CertificateSha256 { get; init; }
    public ConsoleRoute Route { get; init; }
    public WireGuardConfig? WireGuard { get; init; }
    public string? TailscaleAuthKey { get; init; }
    public string? TailscaleTags { get; init; }

    /// <summary>Why the chosen route can't be used as set up, such as a WireGuard config that doesn't read; null when it can.</summary>
    public string? Problem { get; init; }

    /// <summary>Whether a field from the page, rather than the environment, decided any of this.</summary>
    public bool FromPage { get; init; }
}

/// <summary>
/// Holds the current <see cref="ConsoleTarget"/>. Read once at start, and again whenever the Console page saves, which
/// raises <see cref="Changed"/>: the connection is rebuilt, TalkWatch signs in again and the live feed reconnects, with no
/// restart.
/// </summary>
public sealed class ConsoleConnection(IServiceScopeFactory scopes, ChannelSecrets secrets, IOptions<TalkOptions> options) : IDisposable
{
    private readonly SemaphoreSlim _load = new(1, 1);
    private ConsoleTarget? _current;

    /// <summary>Raised after the target changes, once the new one is current.</summary>
    public event Action? Changed;

    /// <summary>The settings from the environment alone, for the page to show what a field falls back to.</summary>
    public TalkOptions FromEnvironment => options.Value;

    /// <summary>The target, read from the database the first time it is asked for.</summary>
    public async ValueTask<ConsoleTarget> GetAsync(CancellationToken cancellationToken) =>
        _current ?? await ReloadAsync(raise: false, cancellationToken);

    /// <summary>Reads the target again, after the Console page saves; raises <see cref="Changed"/> when it differs.</summary>
    public Task<ConsoleTarget> ReloadAsync(CancellationToken cancellationToken) => ReloadAsync(raise: true, cancellationToken);

    private async Task<ConsoleTarget> ReloadAsync(bool raise, CancellationToken cancellationToken)
    {
        await _load.WaitAsync(cancellationToken);
        ConsoleTarget fresh;
        bool changed;
        try
        {
            if (!raise && _current is not null)
            {
                return _current;
            }

            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var saved = await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().ConsoleSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            fresh = Combine(saved, options.Value, secrets);
            changed = _current is not null && _current != fresh;
            _current = fresh;
        }
        finally
        {
            _load.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }

        return fresh;
    }

    /// <summary>The page's fields over the environment's, and whether the chosen route has what it needs.</summary>
    public static ConsoleTarget Combine(ConsoleSettings? saved, TalkOptions env, ChannelSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(secrets);
        var route = saved?.Route ?? env.Route;
        var target = new ConsoleTarget
        {
            Url = saved?.ConsoleUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : env.ConsoleUrl,
            Username = saved?.Username ?? env.Username ?? "",
            Password = secrets.Unprotect(saved?.ProtectedPassword) ?? env.Password ?? "",
            CertificateSha256 = saved?.CertificateSha256 ?? env.CertificateSha256,
            Route = route,
            TailscaleAuthKey = secrets.Unprotect(saved?.ProtectedTailscaleAuthKey) ?? env.TailscaleAuthKey,
            TailscaleTags = saved?.TailscaleTags ?? env.TailscaleTags,
            FromPage = saved is not null,
        };

        if (target.Url is { } address && address.Scheme != Uri.UriSchemeHttps)
        {
            return target with { Problem = $"The console's address {address} isn't https: TalkWatch sends the console's password only over TLS." };
        }

        switch (route)
        {
            case ConsoleRoute.WireGuard:
                var conf = secrets.Unprotect(saved?.ProtectedWireGuardConfig) ?? env.WireGuardConfig;
                if (string.IsNullOrWhiteSpace(conf))
                {
                    return target with { Problem = "WireGuard is chosen, but no WireGuard settings are saved." };
                }

                try
                {
                    var wireGuard = WireGuardConfig.Parse(conf);
                    return target with
                    {
                        WireGuard = wireGuard,
                        Problem = target.Url is { } u && System.Net.IPAddress.TryParse(u.Host, out var ip) && !wireGuard.Routes(ip)
                            ? $"The console's address {u.Host} isn't in the tunnel's allowed addresses ({string.Join(", ", wireGuard.AllowedIps)})."
                            : null,
                    };
                }
                catch (FormatException e)
                {
                    return target with { Problem = "The WireGuard settings don't read: " + e.Message };
                }

            case ConsoleRoute.Tailscale when string.IsNullOrWhiteSpace(target.TailscaleAuthKey):
                return target with { Problem = "Tailscale is chosen, but no auth key is saved." };

            case ConsoleRoute.Tailscale when target.TailscaleAuthKey!.StartsWith("tskey-client-", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(target.TailscaleTags):
                return target with { Problem = "A Tailscale OAuth client secret needs at least one tag, such as tag:talkwatch." };

            default:
                return target;
        }
    }

    public void Dispose() => _load.Dispose();
}

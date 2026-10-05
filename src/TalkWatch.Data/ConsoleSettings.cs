namespace TalkWatch.Data;

/// <summary>How TalkWatch reaches the console.</summary>
public enum ConsoleRoute
{
    /// <summary>Straight to its address: TalkWatch is on the console's network.</summary>
    Direct,

    /// <summary>Through the site gateway's own WireGuard VPN server.</summary>
    WireGuard,

    /// <summary>Through a Tailscale tailnet that a subnet router on the site's network joins.</summary>
    Tailscale,
}

/// <summary>
/// The console and the way to it, as an admin set them on the Console page. Each field left null falls back to the
/// matching Talk__ setting, as the mail and Telegram settings do, so an install can set these in either place, or split
/// them.
/// </summary>
public sealed class ConsoleSettings
{
    public Guid SiteId { get; set; }

    public string? ConsoleUrl { get; set; }
    public string? Username { get; set; }

    /// <summary>Encrypted with the app's data-protection keys, like channel secrets. Never shown again once saved.</summary>
    public string? ProtectedPassword { get; set; }

    public string? CertificateSha256 { get; set; }

    public ConsoleRoute? Route { get; set; }

    /// <summary>The WireGuard client's settings as a .conf, private key and all, so encrypted. Never shown again whole.</summary>
    public string? ProtectedWireGuardConfig { get; set; }

    /// <summary>A Tailscale auth key or OAuth client secret. Encrypted; never shown again once saved.</summary>
    public string? ProtectedTailscaleAuthKey { get; set; }

    /// <summary>The tags TalkWatch's node advertises, comma-separated; an OAuth client secret needs at least one.</summary>
    public string? TailscaleTags { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

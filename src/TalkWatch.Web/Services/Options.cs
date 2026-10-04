namespace TalkWatch.Web.Services;

// Every setting here is described, and docs/configuration.md is generated from these descriptions and defaults: a
// test fails when the two differ, and writes the new version for review.

/// <summary>The console TalkWatch reads from.</summary>
public sealed class TalkOptions
{
    public const string Section = "Talk";

    /// <summary>The console's LAN address, such as https://10.0.0.1. Polling is off while this is unset.</summary>
    public Uri? ConsoleUrl { get; set; }

    /// <summary>A console user with read-only access to Talk, signed in by username (not e-mail address).</summary>
    public string? Username { get; set; }

    /// <summary>That user's password.</summary>
    public string? Password { get; set; }

    /// <summary>
    /// SHA-256 of the console's certificate. Consoles ship a self-signed certificate, so pinning it is how TalkWatch
    /// trusts the console without turning certificate checks off.
    /// </summary>
    public string? CertificateSha256 { get; set; }

    /// <summary>Seconds between polls of the call log; the live feed brings calls between them.</summary>
    public int PollSeconds { get; set; } = 60;

    /// <summary>
    /// Copy the transcripts Talk makes of calls, when its AI transcription is on. They are readable only through a
    /// grant that allows transcripts. False leaves them on the console only.
    /// </summary>
    public bool CopyTranscripts { get; set; } = true;
}

/// <summary>The one site 1.0 runs.</summary>
public sealed class SiteOptions
{
    public const string Section = "Site";

    /// <summary>The site's name, used when it is first created.</summary>
    public string Name { get; set; } = "TalkWatch";

    /// <summary>ISO 3166 region for numbers written nationally.</summary>
    public string Region { get; set; } = "GB";

    /// <summary>IANA time zone for the dashboard, alert time windows and times in alert messages.</summary>
    public string TimeZone { get; set; } = "Europe/London";

    /// <summary>
    /// The address people reach TalkWatch at, such as https://talkwatch.example. Alerts link to it, and acknowledge and
    /// snooze links appear only when it is set.
    /// </summary>
    public Uri? PublicUrl { get; set; }
}

/// <summary>The first admin account, created at start-up when there are no users yet.</summary>
public sealed class BootstrapOptions
{
    public const string Section = "Bootstrap";

    /// <summary>The first admin's username. Ignored once anyone exists, so it can be removed after the first start.</summary>
    public string? AdminUsername { get; set; }

    /// <summary>The first admin's password, at least 12 characters. Ignored once anyone exists.</summary>
    public string? AdminPassword { get; set; }
}

/// <summary>The database password, kept apart from the connection string so that it can be a secret file.</summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Added to ConnectionStrings__TalkWatch, which can then be written without it.</summary>
    public string? Password { get; set; }
}

/// <summary>Where copied recordings and voicemail live.</summary>
public sealed class AudioOptions
{
    public const string Section = "Audio";

    /// <summary>The folder audio is copied to; mount a volume there, and back it up with the database.</summary>
    public string Path { get; set; } = "/data/audio";
}

/// <summary>The reverse proxy in front of TalkWatch.</summary>
public sealed class ProxyOptions
{
    public const string Section = "Proxy";

    /// <summary>
    /// The proxy's networks, comma-separated CIDRs such as 192.168.90.0/24. X-Forwarded-For and X-Forwarded-Proto are
    /// believed only from these, since from anyone else they would let a client pick its own address. Needed behind a
    /// proxy for the sign-in limit to tell visitors apart, and for sign-in through an identity provider.
    /// </summary>
    public string? TrustedNetworks { get; set; }
}

/// <summary>Limits on signing in, on top of locking an account after five wrong passwords.</summary>
public sealed class SignInLimitOptions
{
    public const string Section = "SignIn";

    /// <summary>Sign-in attempts allowed per client address per minute.</summary>
    public int AttemptsPerMinute { get; set; } = 10;
}

/// <summary>
/// The mail server for email alerts and reports; each field can instead be set on the Alert channels page, which wins.
/// Port 465 uses TLS from the first byte; any other port uses STARTTLS when StartTls says so. The ports for reading mail
/// (993, 143, 995, 110) are refused, because a mail server never listens there and the attempt would only time out.
/// TalkWatch greets the server by the host of Site__PublicUrl, or the From address's domain, never the machine's own
/// name: in a container that is a bare id, which strict servers refuse as an invalid HELO name. With a username set and
/// a server that offers sign-in only over TLS, a send fails saying so: turn StartTls on, or clear the username for a
/// server that takes mail without signing in. A failed send is recorded on the alert or report copy and tried again; it
/// never stops TalkWatch. Send test email, on the Alert channels page, sends one at once and shows the server's answer.
/// </summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    /// <summary>The mail server's host name.</summary>
    public string? Host { get; set; }

    /// <summary>The mail server's port.</summary>
    public int Port { get; set; } = 587;

    /// <summary>
    /// Why a port cannot be the mail server's, or null when it may be: the ports for reading mail, which answer a
    /// connection with a TLS handshake or a mailbox greeting that the mail client waits through until it times out.
    /// </summary>
    public static string? PortProblem(int port) => port switch
    {
        993 or 143 => $"Port {port} is for reading mail (IMAP), not sending it. Mail servers take mail on 587, with STARTTLS.",
        995 or 110 => $"Port {port} is for reading mail (POP3), not sending it. Mail servers take mail on 587, with STARTTLS.",
        _ => null,
    };

    /// <summary>The address alerts are sent from.</summary>
    public string? From { get; set; }

    /// <summary>The username, if the server needs one.</summary>
    public string? Username { get; set; }

    /// <summary>The password, if the server needs one.</summary>
    public string? Password { get; set; }

    /// <summary>Whether to use STARTTLS, on any port but 465.</summary>
    public bool StartTls { get; set; } = true;
}

/// <summary>
/// Site-wide Telegram defaults. A Telegram channel uses its own bot token and chat id when it has them, and these when
/// it does not; the alerts page can set them too, and wins.
/// </summary>
public sealed class TelegramOptions
{
    public const string Section = "Telegram";

    /// <summary>The bot token from @BotFather.</summary>
    public string? BotToken { get; set; }

    /// <summary>The chat to send to: a number such as -1001234567890, or @channelname.</summary>
    public string? ChatId { get; set; }
}

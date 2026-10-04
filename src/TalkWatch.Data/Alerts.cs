using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

public enum ChannelKind
{
    /// <summary>A push notification through an ntfy server (self-hosted or ntfy.sh).</summary>
    Ntfy,

    /// <summary>A signed JSON POST to any URL: Home Assistant, n8n, Slack or Teams through a relay, and so on.</summary>
    Webhook,

    /// <summary>An email through the SMTP server in the settings.</summary>
    Email,

    /// <summary>A message from a Telegram bot to a chat, group or channel.</summary>
    Telegram,

    /// <summary>
    /// TalkWatch itself, in its owner's browser: the bell on every page, a pop-up on any page open when it arrives, and
    /// a desktop notification on each browser they have allowed. Always one person's.
    /// </summary>
    Browser,
}

/// <summary>Where alerts go. A channel owned by a person only receives alerts about what that person may see.</summary>
public sealed class AlertChannel
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public required string Name { get; set; }
    public ChannelKind Kind { get; set; }

    /// <summary>
    /// For a channel TalkWatch keeps for one of Talk's contacts, so a flow can notify them: the contact's uuid. Its
    /// address follows what Talk holds for them, and it is not listed with the channels people set up.
    /// </summary>
    public string? ContactUuid { get; set; }

    /// <summary>
    /// The ntfy topic URL (https://ntfy.example/topic), the webhook URL, the email address, or the Telegram chat id
    /// (empty to use Telegram__ChatId).
    /// </summary>
    public required string Target { get; set; }

    /// <summary>
    /// The ntfy access token, the webhook signing secret or the Telegram bot token (none to use Telegram__BotToken),
    /// encrypted with the app's data-protection keys. Never shown again once saved.
    /// </summary>
    public string? ProtectedSecret { get; set; }

    /// <summary>
    /// A person whose grants bound what this channel receives, or null for a site channel that receives everything its
    /// rules match. Only admins create channels either way.
    /// </summary>
    public Guid? OwnerUserId { get; set; }

    /// <summary>
    /// Weekly quiet hours, same shape as a rule's window: alerts due inside them are held until they end, not dropped.
    /// Null days for none.
    /// </summary>
    public int? QuietDays { get; set; }
    public TimeOnly? QuietStart { get; set; }
    public TimeOnly? QuietEnd { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// What happens when something is alerted on: a <see cref="FlowDefinition"/>, stored as JSON. The trigger is kept as a
/// column as well, so an alert only reads the flows it could start.
/// </summary>
public sealed class AlertFlow
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public required string Name { get; set; }
    public AlertEventType Trigger { get; set; }

    /// <summary>The flow as <see cref="Flows.Write"/> writes it.</summary>
    public required string Definition { get; set; }

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>A Manager who made the flow for their own lines, or null for a flow an admin made for the site.</summary>
    public Guid? OwnerUserId { get; set; }
}

public enum FlowRunState
{
    /// <summary>A stage is still to come.</summary>
    Running,

    /// <summary>Every stage has run.</summary>
    Done,

    /// <summary>Acknowledged, or its flow turned off or removed, before the last stage.</summary>
    Stopped,
}

/// <summary>
/// One flow running for one alert. The stages are worked out when the alert is raised and kept here, so changing a
/// flow changes what later alerts do, not one already under way.
/// </summary>
public sealed class AlertFlowRun
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid EventId { get; set; }
    public Guid FlowId { get; set; }

    /// <summary>The stages, as <see cref="Flows.WritePlan"/> writes them.</summary>
    public required string Plan { get; set; }

    /// <summary>The stage to run next; the count of stages once done.</summary>
    public int NextStage { get; set; }

    /// <summary>When the next stage runs, unless the alert is acknowledged first.</summary>
    public DateTimeOffset? DueAt { get; set; }

    public FlowRunState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Something that happened and may be alerted on, recorded once: the key makes a repeat of the same event a no-op.</summary>
public sealed class AlertEvent
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public AlertEventType Type { get; set; }

    /// <summary>Unique per site, such as 'call:{uuid}:MissedCall' or 'device:{mac}:offline:{time}'.</summary>
    public required string Key { get; set; }

    public DateTimeOffset At { get; set; }
    public Guid? CallId { get; set; }

    /// <summary>For a handset event, the user it is assigned to, which decides who may hear of it.</summary>
    public string? UserUuid { get; set; }

    public required string Title { get; set; }
    public required string Message { get; set; }

    /// <summary>Someone has it in hand: nothing more is sent about it and it does not escalate.</summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    /// <summary>Who acknowledged it: a person's name, or "link" for a link in a notification.</summary>
    public string? AcknowledgedBy { get; set; }

    /// <summary>Escalation waits until at least this time.</summary>
    public DateTimeOffset? SnoozedUntil { get; set; }
}

public enum DeliveryState
{
    Pending,
    Sent,

    /// <summary>Gave up after the last retry.</summary>
    Dead,

    /// <summary>Not sent: the alert was acknowledged first, or, for an acknowledgement, the channel was in quiet hours.</summary>
    Cancelled,
}

/// <summary>One alert to one channel: the outbox. Retried with back-off until sent or given up.</summary>
public sealed class AlertDelivery
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid EventId { get; set; }
    public Guid FlowId { get; set; }
    public Guid ChannelId { get; set; }

    /// <summary>The flow's stage that sent it: 0 at first, more after a wait nobody acknowledged.</summary>
    public int Stage { get; set; }

    /// <summary>Sent at high priority, where the channel has one.</summary>
    public bool Urgent { get; set; }

    /// <summary>
    /// Not the alert but word that someone has acknowledged it, to a channel the alert reached: the person who pressed
    /// the button sees it worked, and everyone else knows it is in hand.
    /// </summary>
    public bool Acknowledgement { get; set; }

    /// <summary>
    /// What the flow sends with it about the call (<c>NotifyIncludes</c>): the summary, what was said, the voicemail. Read
    /// when it is sent, through its channel owner's access.
    /// </summary>
    public int Include { get; set; }

    /// <summary>
    /// Set when the stage gathers what it sends: deliveries to the same channel with the same key, due at the same time,
    /// go as one message. The flow's id and the stage, so two flows' bundles never mix.
    /// </summary>
    public string? BundleKey { get; set; }

    public DeliveryState State { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    public string? LastError { get; set; }

    /// <summary>
    /// From an outside-contact recipient: a call put through to a phone outside, so worth sending only while it can still
    /// be picked up. Never to email, and in the channel's quiet hours dropped rather than held, unless urgent.
    /// </summary>
    public bool Outside { get; set; }

    /// <summary>
    /// The channel's owner may not see the call's line, but was put through the call outside: they hear who is calling
    /// and on which line, and nothing more. No summary, transcript or voicemail, and no links to the call or the alert.
    /// </summary>
    public bool CallerOnly { get; set; }

    /// <summary>For a browser alert, when its owner saw it in the bell's list: unread until then.</summary>
    public DateTimeOffset? SeenAt { get; set; }
}

/// <summary>
/// A browser someone has allowed desktop notifications in: where its push service takes messages, and the keys that
/// encrypt them so only that browser can read them.
/// </summary>
/// <summary>
/// A person in TalkWatch who stands behind one of Talk's outside contacts: whoever carries that mobile. One contact can
/// be linked to several people, as an on-call phone passed between staff is, and one person to several contacts. An
/// outside-contact recipient reaches the people linked to the contacts a call is put through to.
/// </summary>
public sealed class ContactLink
{
    public Guid SiteId { get; set; }

    /// <summary>Talk's uuid for the contact.</summary>
    public required string ContactUuid { get; set; }

    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
}

public sealed class PushSubscriptionRow
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid UserId { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The site's mail server and Telegram defaults as set in the admin UI. Each field left null falls back to the
/// matching Smtp__ or Telegram__ setting, so an install can set these in either place, or split them.
/// </summary>
public sealed class AlertSettings
{
    public Guid SiteId { get; set; }

    public string? SmtpHost { get; set; }
    public int? SmtpPort { get; set; }
    public string? SmtpFrom { get; set; }
    public string? SmtpUsername { get; set; }

    /// <summary>Encrypted with the app's data-protection keys, like channel secrets. Never shown again once saved.</summary>
    public string? SmtpProtectedPassword { get; set; }

    public bool? SmtpStartTls { get; set; }

    /// <summary>Encrypted with the app's data-protection keys. Never shown again once saved.</summary>
    public string? TelegramProtectedBotToken { get; set; }

    public string? TelegramChatId { get; set; }

    /// <summary>
    /// The key pair that signs desktop notifications (VAPID), made the first time one is needed. Browsers that allowed
    /// notifications trust the public key, so it is kept, not remade.
    /// </summary>
    public string? VapidPublicKey { get; set; }

    /// <summary>Encrypted with the app's data-protection keys.</summary>
    public string? VapidProtectedPrivateKey { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

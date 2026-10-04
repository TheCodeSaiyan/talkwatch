using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>
/// An outside Contact ticked as an answering line: an assistant or answering service whose voicemail may take a call
/// Talk forwards to it. Calls it answers are read against its greeting phrases once their transcript arrives
/// (<see cref="OutsideVoicemail"/>).
/// </summary>
public sealed class AnsweringLine
{
    public Guid SiteId { get; set; }

    /// <summary>The Talk Contact's id, as a call's answered_by_contact_id has it.</summary>
    public required string ContactId { get; set; }

    /// <summary>The provider's greeting phrases, one to a line.</summary>
    public string Phrases { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }

    public static IReadOnlyList<string> PhrasesOf(string phrases) =>
        [.. phrases.Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0)];
}

/// <summary>
/// What a call an answering line took turned out to be: read from its transcript, or set by hand. It outlives every
/// re-read of the call from Talk, which lays it over the outcome again (<see cref="CallOutcomes.With"/>).
/// </summary>
public sealed class CallFinding
{
    public Guid CallId { get; set; }

    public Guid SiteId { get; set; }

    public OutsideFinding Finding { get; set; }

    /// <summary>The Contact that answered, as on the call.</summary>
    public string? ContactId { get; set; }

    /// <summary>The greeting phrase that matched; null when a person answered, or it was set by hand.</summary>
    public string? Phrase { get; set; }

    /// <summary>Set by a person rather than read from the transcript; the transcript then never overrides it.</summary>
    public bool ByHand { get; set; }

    /// <summary>Who set it by hand.</summary>
    public string? DecidedBy { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}

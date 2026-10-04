using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>
/// A console TalkWatch reads from. Every other table carries its id: 1.0 runs one site, but adding tenancy to a schema
/// afterwards is far more expensive than carrying the column from the start.
/// </summary>
public sealed class Site
{
    public Guid Id { get; set; }
    public required string Name { get; set; }

    /// <summary>ISO 3166 region used to read numbers written nationally, such as GB.</summary>
    public required string DefaultRegion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CallRow
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }

    /// <summary>Talk's own id for the call, unique within a site.</summary>
    public required string TalkUuid { get; set; }

    public DateTimeOffset Time { get; set; }
    public required string Direction { get; set; }
    public required string Status { get; set; }

    /// <summary>What happened to the call, worked out from its events when it is stored (<see cref="CallOutcomes.Of"/>).</summary>
    public CallOutcome Outcome { get; set; }
    public int DurationSeconds { get; set; }

    // Numbers are kept as Talk wrote them and in E.164 beside them: matching uses E.164, display uses the raw form,
    // and internal numbers and short codes have no E.164 at all.
    public string? FromRaw { get; set; }
    public string? FromE164 { get; set; }
    public string? ToRaw { get; set; }
    public string? ToE164 { get; set; }
    public string? AnsweredByRaw { get; set; }
    public string? AnsweredByE164 { get; set; }
    public string? CallerName { get; set; }

    public bool HasRecording { get; set; }
    public string? RecordingFilename { get; set; }
    public string? Country { get; set; }
    public int? QualityScore { get; set; }

    /// <summary>For a missed or voicemail call: when the caller was got back to, and how (<see cref="CallBacks"/>).</summary>
    public DateTimeOffset? ReturnedAt { get; set; }
    public CallBackHow? ReturnedHow { get; set; }

    /// <summary>Who marked it done, when someone did.</summary>
    public string? ReturnedBy { get; set; }

    /// <summary>Who is to ring the caller back, when a flow has given the call-back to someone; null when nobody has it.</summary>
    public Guid? CallBackAssignedTo { get; set; }

    public DateTimeOffset? CallBackAssignedAt { get; set; }

    public DateTimeOffset IngestedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<CallEventRow> Events { get; set; } = [];
    public List<CallLine> Lines { get; set; } = [];
}

/// <summary>A routing event, with its data kept whole as JSON so nothing Talk sent is lost.</summary>
public sealed class CallEventRow
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid CallId { get; set; }
    public int Sequence { get; set; }
    public DateTimeOffset Time { get; set; }
    public required string Event { get; set; }
    public string? EventUuid { get; set; }
    public string? DataJson { get; set; }
}

/// <summary>A line a call touched. Grants are resolved against these rows.</summary>
public sealed class CallLine
{
    public Guid SiteId { get; set; }
    public Guid CallId { get; set; }
    public LineKind Kind { get; set; }
    public required string Key { get; set; }
}

/// <summary>
/// A response exactly as Talk sent it. When a firmware update changes a shape and a fix ships, these let the data be
/// read again rather than lost.
/// </summary>
public sealed class RawPayload
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public required string Endpoint { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public required string Body { get; set; }
}

/// <summary>
/// What the console was running, recorded whenever it changes. When Talk's data changes shape, this says which update
/// came just before it.
/// </summary>
public sealed class ConsoleVersionRecord
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public DateTimeOffset SeenAt { get; set; }
    public string? UnifiOs { get; set; }
    public string? UnifiOsChannel { get; set; }
    public string? Talk { get; set; }
    public string? TalkChannel { get; set; }
}

/// <summary>
/// A line the console knows about, with its name: the list a grants screen offers, and how calls show who was
/// involved. Kept in sync from the console; lines that disappear are kept, marked absent, since old calls still name them.
/// </summary>
public sealed class LineRecord
{
    public Guid SiteId { get; set; }
    public LineKind Kind { get; set; }
    public required string Key { get; set; }
    public required string Name { get; set; }
    public string? Ext { get; set; }
    public bool Present { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A personal token for the read-only API. It acts as its owner, seeing exactly what they may see. Only a hash is kept:
/// the token itself is shown once, when it is made.
/// </summary>
public sealed class ApiToken
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid UserId { get; set; }
    public required string Name { get; set; }

    /// <summary>SHA-256 of the token, hex.</summary>
    public required string Hash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
}

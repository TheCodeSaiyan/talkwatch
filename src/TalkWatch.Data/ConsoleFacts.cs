namespace TalkWatch.Data;

/// <summary>
/// Something TalkWatch last saw about the console, kept so a change is noticed across restarts: the account's problems,
/// whether recording is on. Named facts with a text value.
/// </summary>
public sealed class ConsoleFact
{
    public Guid SiteId { get; set; }
    public required string Name { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

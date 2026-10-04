namespace TalkWatch.Core.Talk;

/// <summary>
/// The Talk account's standing (GET /proxy/talk/api/install), as Talk 5.3.2 sends it: whether it is active, can call,
/// has paid, and is free of blocks. The usage fields are left out until a capture shows their units.
/// </summary>
public sealed record TalkAccount
{
    public string? Status { get; init; }
    public string? CallingStatus { get; init; }
    public string? PaymentStatus { get; init; }
    public bool? Blocked { get; init; }
    public bool? UnauthorizedUsage { get; init; }
    public bool? EmergencyModeActive { get; init; }

    /// <summary>What is wrong, in words, one per problem; empty when all is well. Each is stable while it lasts.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Status is { } status && status != "active")
        {
            problems.Add($"The Talk account is {status}, not active.");
        }

        if (CallingStatus is { } calling && calling != "active")
        {
            problems.Add($"Calling is {calling}, not active.");
        }

        if (PaymentStatus is { } payment && payment != "succeeded")
        {
            problems.Add($"The last payment {(payment == "failed" ? "failed" : $"is {payment}")}.");
        }

        if (Blocked == true)
        {
            problems.Add("The Talk account is blocked.");
        }

        if (UnauthorizedUsage == true)
        {
            problems.Add("Talk reports unauthorised use of the account.");
        }

        if (EmergencyModeActive == true)
        {
            problems.Add("Talk is in emergency mode.");
        }

        return problems;
    }
}

/// <summary>The Talk settings TalkWatch depends on (GET /proxy/talk/api/setting/config).</summary>
public sealed record TalkSettings
{
    public bool? CallLogRecordingEnabled { get; init; }
    public bool? AiCallTranscriptionsEnabled { get; init; }
}

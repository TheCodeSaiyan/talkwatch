using System.Globalization;
using System.Text;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Web.Services;

/// <summary>
/// An alert as an ntfy notification shows it on a phone: who and what in the title, then the caller's number and the
/// line they rang, when and for how long, and the flow that sent it, each on its own line. Plain text: the ntfy phone
/// app shows Markdown as it is typed. An alert about no call keeps its own title and message.
/// </summary>
public static class NtfyText
{
    /// <summary>What a notification says about the call: the caller as Talk named them, their number, the line rung.</summary>
    public sealed record CallFacts(string? Name, string? Number, string? Line, DateTimeOffset Time, int DurationSeconds);

    public static (string Title, string Body) Compose(AlertEventType type, string title, string message, CallFacts? call, string? flow, TimeZoneInfo zone,
        bool escalation, bool acknowledgement, string? acknowledged)
    {
        var heading = call is null ? null : Heading(type, Who(call));
        var body = new StringBuilder();
        if (heading is null || call is null)
        {
            heading = title;
            body.Append(message);
        }
        else
        {
            // The number under a name; with no name the title already shows it, so only the line rung.
            var number = NumberNormaliser.Display(call.Number);
            var line = call.Line is { Length: > 0 } rung ? rung : null;
            var first = call.Name is not null && number is not null ? (line is null ? number : $"{number} → {line}") : line is null ? null : $"To {line}";
            if (first is not null)
            {
                body.Append(first).Append('\n');
            }

            var local = TimeZoneInfo.ConvertTime(call.Time, zone);
            body.Append(local.ToString("ddd HH:mm", CultureInfo.InvariantCulture));
            if (Detail(type, call.DurationSeconds) is { } detail)
            {
                body.Append(" · ").Append(detail);
            }
        }

        if (flow is { Length: > 0 })
        {
            body.Append("\nFlow: ").Append(flow);
        }

        if (acknowledgement && acknowledged is { Length: > 0 })
        {
            body.Append('\n').Append(acknowledged);
        }

        var prefix = acknowledgement ? "Acknowledged: " : escalation ? "Still not picked up: " : "";
        return (prefix + heading, body.ToString());
    }

    // The caller as a person reads them: Talk's name for them, else their number, else that it was withheld.
    private static string Who(CallFacts call) => call.Name ?? NumberNormaliser.Display(call.Number) ?? "a withheld number";

    private static string? Heading(AlertEventType type, string who) => type switch
    {
        AlertEventType.MissedCall => $"Missed call from {who}",
        AlertEventType.Voicemail => Capital($"{who} left a voicemail"),
        AlertEventType.VoicemailTranscribed => $"Voicemail from {who}, transcribed",
        AlertEventType.InboundCall => Capital($"{who} is calling"),
        AlertEventType.HungUpAtSwitchboard => Capital($"{who} hung up in the menu"),
        AlertEventType.NegativeCall => $"A call with {who} was rated negative",
        AlertEventType.PoorQualityCall => $"Poor call quality with {who}",
        _ => null,
    };

    // How long, in the alert's own terms: a missed caller gave up, a switchboard hang-up spent it in the menu.
    private static string? Detail(AlertEventType type, int seconds) => seconds <= 0 ? null : type switch
    {
        AlertEventType.MissedCall => $"gave up after {Duration(seconds)}",
        AlertEventType.HungUpAtSwitchboard => $"after {Duration(seconds)} in the menu",
        AlertEventType.NegativeCall or AlertEventType.PoorQualityCall => $"lasted {Duration(seconds)}",
        _ => null,
    };

    private static string Duration(int seconds) => seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60:00}s";

    private static string Capital(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>
    /// A header value as ntfy takes it: as it is when plain ASCII, else RFC 2047 encoded, which ntfy decodes. HTTP
    /// headers carry ASCII, and a caller's name or a line's often is not.
    /// </summary>
    public static string Header(string value) =>
        value.All(c => c is >= ' ' and <= '~') ? value : $"=?UTF-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
}

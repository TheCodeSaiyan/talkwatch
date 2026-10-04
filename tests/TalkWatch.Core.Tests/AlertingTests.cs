using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Tests;

public class AlertingTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    private static readonly TimeWindow OfficeHours = new(TimeWindow.Weekdays, new TimeOnly(9, 0), new TimeOnly(17, 30), Outside: false);

    // Event sequences as a real console recorded them. Talk marked every one of these inbound calls 'accepted'.
    private const string Answered = "call_started seq_call_trying_endpoints call_accepted call_hangup";
    private const string RangOut = "call_started seq_call_trying_endpoints call_hangup";
    private const string VoicemailLeft = "call_started keypress entered_sa_menu call_sent_to_voicemail vm_msg_recorded call_hangup";
    private const string VoicemailAbandoned = "call_started seq_call_trying_endpoints call_sent_to_voicemail vm_recording_canceled call_hangup";
    private const string HungUpInMenu = "call_started entered_sa_menu call_hangup";
    private const string HungUpAtGreeting = "call_started call_hangup";
    private const string StillRinging = "call_started seq_call_trying_endpoints";

    [Theory]
    [InlineData("in", "accepted", Answered, new[] { AlertEventType.InboundCall })]
    [InlineData("in", "accepted", RangOut, new[] { AlertEventType.InboundCall, AlertEventType.MissedCall })]
    [InlineData("in", "accepted", VoicemailLeft, new[] { AlertEventType.InboundCall, AlertEventType.Voicemail })]
    [InlineData("in", "accepted", VoicemailAbandoned, new[] { AlertEventType.InboundCall, AlertEventType.MissedCall })]
    [InlineData("in", "accepted", HungUpInMenu, new[] { AlertEventType.InboundCall, AlertEventType.HungUpAtSwitchboard })]
    [InlineData("in", "accepted", HungUpAtGreeting, new[] { AlertEventType.InboundCall, AlertEventType.HungUpAtSwitchboard })]
    [InlineData("in", "ringing", StillRinging, new[] { AlertEventType.InboundCall })]
    [InlineData("in", "cancelled", "", new[] { AlertEventType.InboundCall, AlertEventType.MissedCall })]
    [InlineData("in", "timed_out", "", new[] { AlertEventType.InboundCall, AlertEventType.MissedCall })]
    [InlineData("in", "blocked", HungUpAtGreeting, new AlertEventType[0])]
    [InlineData("out", "cancelled", RangOut, new AlertEventType[0])]
    [InlineData("internal", "accepted", RangOut, new AlertEventType[0])]
    public void Calls_raise_events_by_what_happened_to_them(string direction, string status, string events, AlertEventType[] expected) =>
        Assert.Equal(expected, CallAlerts.For(direction, status, events.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    // Two calls on a real console whose hang-up was never recorded, long after they ended.
    [Theory]
    [InlineData("call_started keypress entered_sa_menu seq_call_trying_endpoints call_sent_to_voicemail", CallOutcome.Missed)]
    [InlineData("call_started", CallOutcome.HungUpAtSwitchboard)]
    [InlineData("call_started seq_call_trying_endpoints call_accepted", CallOutcome.Answered)]
    public void A_call_long_over_has_ended_whether_or_not_its_hang_up_was_recorded(string events, CallOutcome outcome)
    {
        var names = events.Split(' ');

        Assert.Equal(outcome, CallOutcomes.Of("in", "accepted", names, longOver: true));
        Assert.Equal(outcome == CallOutcome.Answered ? CallOutcome.Answered : CallOutcome.InProgress, CallOutcomes.Of("in", "accepted", names));
    }

    [Theory]
    [InlineData("2026-09-30T08:00:00Z", true)]   // Wednesday 09:00 BST: inside
    [InlineData("2026-09-30T16:29:00Z", true)]   // 17:29 BST
    [InlineData("2026-09-30T16:30:00Z", false)]  // 17:30 BST: the end is exclusive
    [InlineData("2026-09-30T07:59:00Z", false)]  // 08:59 BST
    [InlineData("2026-10-03T10:00:00Z", false)]  // Saturday
    [InlineData("2026-12-02T09:30:00Z", true)]   // Wednesday 09:30 GMT: the window follows the clocks
    public void An_office_hours_window_follows_the_sites_local_time(string at, bool inside) =>
        Assert.Equal(inside, OfficeHours.Matches(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), London));

    [Fact]
    public void Outside_turns_a_window_into_an_after_hours_rule()
    {
        var afterHours = OfficeHours with { Outside = true };

        Assert.True(afterHours.Matches(DateTimeOffset.Parse("2026-09-30T19:00:00Z", System.Globalization.CultureInfo.InvariantCulture), London));
        Assert.False(afterHours.Matches(DateTimeOffset.Parse("2026-09-30T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture), London));
    }

    [Theory]
    [InlineData("2026-09-30T22:30:00Z", true)]   // Wednesday 23:30 BST
    [InlineData("2026-10-01T04:00:00Z", true)]   // Thursday 05:00 BST, the morning after a listed Wednesday
    [InlineData("2026-10-01T06:00:00Z", false)]  // Thursday 07:00 BST
    [InlineData("2026-10-04T22:30:00Z", false)]  // Sunday night: not listed
    public void An_overnight_window_spans_midnight(string at, bool inside)
    {
        var nights = new TimeWindow(TimeWindow.Weekdays, new TimeOnly(22, 0), new TimeOnly(6, 0), Outside: false);

        Assert.Equal(inside, nights.Matches(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture), London));
    }

    [Fact]
    public void Days_round_trip_through_a_mask() =>
        Assert.Equal(TimeWindow.Weekdays, TimeWindow.FromMask(TimeWindow.ToMask(TimeWindow.Weekdays)));
}

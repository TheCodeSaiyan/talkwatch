using TalkWatch.Core.Alerts;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>An alert laid out for a phone: who and what in the title, then the number and line, when, and the flow.</summary>
public sealed class NtfyTextTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    // Saturday 3 October 2026, 23:00 in London.
    private static readonly DateTimeOffset Saturday = new(2026, 10, 3, 22, 0, 0, TimeSpan.Zero);

    private static NtfyText.CallFacts Call(string? name, string? number = "+447700900111", string? line = "Main line", int seconds = 12) =>
        new(name, number, line, Saturday, seconds);

    [Fact]
    public void A_named_caller_leads_the_title_and_their_number_and_line_lead_the_body()
    {
        var (title, body) = NtfyText.Compose(AlertEventType.HungUpAtSwitchboard, "Hung up at the switchboard", "From …", Call("Nigel Tatschner"),
            "Repeat Caller Within 1 Minute", London, escalation: false, acknowledgement: false, acknowledged: null);

        Assert.Equal("Nigel Tatschner hung up in the menu", title);
        Assert.Equal("+44 7700 900111 → Main line\nSat 23:00 · after 12s in the menu\nFlow: Repeat Caller Within 1 Minute", body);
    }

    [Fact]
    public void With_no_name_the_title_carries_the_number_and_the_body_only_the_line()
    {
        var (title, body) = NtfyText.Compose(AlertEventType.MissedCall, "Missed call", "From …", Call(null, seconds: 75), null, London, false, false, null);

        Assert.Equal("Missed call from +44 7700 900111", title);
        Assert.Equal("To Main line\nSat 23:00 · gave up after 1m 15s", body);
    }

    [Fact]
    public void A_withheld_caller_says_so_and_a_sentence_still_starts_with_a_capital()
    {
        var (title, _) = NtfyText.Compose(AlertEventType.Voicemail, "New voicemail", "", Call(null, number: null), null, London, false, false, null);

        Assert.Equal("A withheld number left a voicemail", title);
    }

    [Fact]
    public void A_reminder_and_an_acknowledgement_say_which_they_are()
    {
        var (still, _) = NtfyText.Compose(AlertEventType.MissedCall, "Missed call", "", Call("Ellie Marsh"), null, London, escalation: true, false, null);
        var (done, body) = NtfyText.Compose(AlertEventType.MissedCall, "Missed call", "", Call("Ellie Marsh"), null, London, false, acknowledgement: true,
            acknowledged: "Grace has it.");

        Assert.Equal("Still not picked up: Missed call from Ellie Marsh", still);
        Assert.Equal("Acknowledged: Missed call from Ellie Marsh", done);
        Assert.EndsWith("\nGrace has it.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void An_alert_about_no_call_keeps_its_own_words()
    {
        var (title, body) = NtfyText.Compose(AlertEventType.HandsetOffline, "Handset offline", "Front desk went offline.", null, "Handsets", London, false, false, null);

        Assert.Equal(("Handset offline", "Front desk went offline.\nFlow: Handsets"), (title, body));
    }

    [Theory]
    [InlineData("Missed call from Ellie Marsh", "Missed call from Ellie Marsh")]
    [InlineData("Missed call from Zoë", "=?UTF-8?B?TWlzc2VkIGNhbGwgZnJvbSBab8Or?=")]
    public void A_header_is_sent_as_it_is_when_ascii_and_encoded_when_not(string value, string header) =>
        Assert.Equal(header, NtfyText.Header(value));
}

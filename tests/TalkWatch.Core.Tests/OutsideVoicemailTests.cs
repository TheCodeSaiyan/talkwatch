using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;

namespace TalkWatch.Core.Tests;

/// <summary>A forward to an outside answering line, told from its transcript: a person, a message left, or a hang-up.</summary>
public sealed class OutsideVoicemailTests
{
    private static readonly string[] Greeting = ["is not available", "leave a message after the tone"];

    private static TranscriptLine L(string text, string? speaker = null) => new(speaker, text, null, null);

    [Fact]
    public void No_greeting_phrase_means_a_person_answered()
    {
        var found = OutsideVoicemail.Judge([L("Hello, Morgan speaking."), L("Hi, it's about the invoice from last week.")], Greeting);

        Assert.Equal((OutsideFinding.Person, null), found);
    }

    [Fact]
    public void Words_after_the_greeting_are_a_message_left()
    {
        var found = OutsideVoicemail.Judge(
            [L("The person you have called is not available. Please leave a message after the tone."), L("Hi, it's Ellie, could you ring me back about the booking?")],
            Greeting);

        Assert.Equal((OutsideFinding.MessageLeft, "leave a message after the tone"), found);
    }

    [Fact]
    public void Nothing_after_the_greeting_is_a_hang_up_at_it()
    {
        var found = OutsideVoicemail.Judge([L("The person you have called is not available. Please leave a message after the tone.")], Greeting);

        Assert.Equal((OutsideFinding.HungUpAtGreeting, "leave a message after the tone"), found);
    }

    [Fact]
    public void A_phrase_matches_without_case_or_punctuation_and_with_a_word_or_two_between()
    {
        var found = OutsideVoicemail.Judge([L("Sorry, Morgan is NOT currently available!"), L("Morgan, call me back when you can please.")], ["is not available"]);

        Assert.Equal(OutsideFinding.MessageLeft, found?.Finding);
    }

    // Seen on a real line: the O2 greeting is transcribed "02", and "o2 messaging" as typed matched nothing.
    [Theory]
    [InlineData("O2 messaging service")]
    [InlineData("o2 messaging")]
    [InlineData("02 messaging service")]
    public void The_letter_o_and_the_digit_zero_are_the_same(string phrase)
    {
        var found = OutsideVoicemail.Judge(
            [L("To the 02 messaging service, the person you are calling is unable to take your call.", "Provider"), L("Hello, this is Ian, please call me back.", "Caller")],
            [phrase]);

        Assert.Equal((OutsideFinding.MessageLeft, phrase), found);
    }

    [Fact]
    public void Three_words_between_is_too_many()
    {
        var found = OutsideVoicemail.Judge([L("It is not, sadly for us all, available today at all.")], ["is not available"]);

        Assert.Equal(OutsideFinding.Person, found?.Finding);
    }

    [Fact]
    public void The_greeting_speaker_carrying_on_is_not_the_caller()
    {
        // The provider reads on after its greeting; only the caller's own words count as a message.
        var hungUp = OutsideVoicemail.Judge(
            [L("The person you have called is not available.", "Provider"), L("To leave a callback number press two, or simply stay on the line.", "Provider")],
            Greeting);
        var left = OutsideVoicemail.Judge(
            [L("The person you have called is not available.", "Provider"), L("Hello, it's Sam from the surgery, please call back.", "Caller")],
            Greeting);

        Assert.Equal(OutsideFinding.HungUpAtGreeting, hungUp?.Finding);
        Assert.Equal(OutsideFinding.MessageLeft, left?.Finding);
    }

    [Fact]
    public void The_greeting_ends_at_the_furthest_phrase_found()
    {
        // "is not available" comes first, but the greeting runs on to "after the tone": what follows that is the caller's.
        var found = OutsideVoicemail.Judge([L("Morgan is not available, so please leave a message after the tone. Thanks")], Greeting);

        Assert.Equal((OutsideFinding.HungUpAtGreeting, "leave a message after the tone"), found);
    }

    [Fact]
    public void Too_short_to_tell_or_no_phrases_leaves_the_call_alone()
    {
        Assert.Null(OutsideVoicemail.Judge([L("Hello?")], Greeting));
        Assert.Null(OutsideVoicemail.Judge([L("Hello, Morgan speaking, how can I help?")], []));
        Assert.Null(OutsideVoicemail.Judge([L("Hello, Morgan speaking, how can I help?")], ["  ", "!"]));
    }

    [Theory]
    [InlineData(CallOutcome.Answered, OutsideFinding.MessageLeft, CallOutcome.OutsideVoicemail)]
    [InlineData(CallOutcome.Answered, OutsideFinding.HungUpAtGreeting, CallOutcome.OutsideMissed)]
    [InlineData(CallOutcome.Answered, OutsideFinding.Person, CallOutcome.Answered)]
    [InlineData(CallOutcome.OutsideVoicemail, OutsideFinding.Person, CallOutcome.Answered)]
    [InlineData(CallOutcome.OutsideMissed, OutsideFinding.MessageLeft, CallOutcome.OutsideVoicemail)]
    // A finding only ever changes an answered call: Talk's own missed calls and voicemail stay as they are.
    [InlineData(CallOutcome.Missed, OutsideFinding.MessageLeft, CallOutcome.Missed)]
    [InlineData(CallOutcome.Voicemail, OutsideFinding.HungUpAtGreeting, CallOutcome.Voicemail)]
    [InlineData(CallOutcome.InProgress, OutsideFinding.MessageLeft, CallOutcome.InProgress)]
    public void A_finding_changes_only_an_answered_call(CallOutcome outcome, OutsideFinding finding, CallOutcome expected) =>
        Assert.Equal(expected, outcome.With(finding));

    [Fact]
    public void The_outside_outcomes_count_as_voicemail_and_missed()
    {
        Assert.True(CallOutcome.OutsideVoicemail.IsVoicemail());
        Assert.True(CallOutcome.OutsideMissed.IsMissed());
        Assert.True(CallOutcome.OutsideVoicemail.IsUnanswered() && CallOutcome.OutsideMissed.IsUnanswered());
        Assert.False(CallOutcome.Answered.IsUnanswered());
    }
}

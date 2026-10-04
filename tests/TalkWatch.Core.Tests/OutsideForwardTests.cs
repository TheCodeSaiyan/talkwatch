using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;

namespace TalkWatch.Core.Tests;

/// <summary>A call Talk puts through to one of its outside contacts, and the flows that tell whoever carries that phone.</summary>
public class OutsideForwardTests
{
    private static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");

    private static FlowFacts Incoming(bool forwarded) =>
        new(AlertEventType.InboundCall, DateTimeOffset.Parse("2026-10-04T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            new HashSet<LineRef> { new(LineKind.Did, "+441144960042") }, IsCall: true, "+447700900123", London) { ForwardedOutside = forwarded };

    [Fact]
    public void The_contacts_a_call_was_put_through_to_are_those_Talk_rang_and_the_one_that_answered_never_those_it_skipped()
    {
        (string, string?)[] events =
        [
            ("call_started", """{"to": "+441144960042", "to_contact_id": 3}"""),
            ("skipped_endpoints", """{"contact_uuids": ["skipped"]}"""),
            ("seq_call_trying_endpoints", """{"contact_uuids": ["rang-1", "rang-2"]}"""),
            ("seq_call_trying_endpoints", """{"contact_uuids": ["rang-1"]}"""),
            ("call_accepted", """{"accepted_by_contact_id": 3, "accepted_by_contact_uuid": "answered"}"""),
            ("call_hangup", null),
        ];

        Assert.Equal(["rang-1", "rang-2", "answered"], CallRouting.ContactsTried(events));
        Assert.Empty(CallRouting.ContactsTried([("call_started", "{}"), ("seq_call_trying_endpoints", "{}"), ("call_accepted", """{"accepted_by": "101"}""")]));
    }

    [Fact]
    public void Who_answered_is_an_outside_contact_by_uuid_and_id_or_someone_on_the_system_or_nobody()
    {
        Assert.Equal((true, "mobile", "3"), CallRouting.WhoAnswered(
            [("seq_call_trying_endpoints", """{"contact_uuids": ["mobile"]}"""), ("call_accepted", """{"accepted_by_contact_id": 3, "accepted_by_contact_uuid": "mobile"}""")]));
        Assert.Equal((true, (string?)null, (string?)null), CallRouting.WhoAnswered([("call_accepted", """{"accepted_by": "101"}""")]));
        Assert.Equal((false, (string?)null, (string?)null), CallRouting.WhoAnswered([("call_started", "{}"), ("call_hangup", null)]));
    }

    [Fact]
    public void The_forwarded_condition_holds_on_calls_put_through_outside_or_not_and_never_off_a_call()
    {
        Assert.True(new ForwardedOutsideCondition(true).Matches(Incoming(forwarded: true)));
        Assert.False(new ForwardedOutsideCondition(true).Matches(Incoming(forwarded: false)));
        Assert.True(new ForwardedOutsideCondition(false).Matches(Incoming(forwarded: false)));
        Assert.False(new ForwardedOutsideCondition(false).Matches(Incoming(forwarded: false) with { IsCall = false }));
    }

    [Fact]
    public void Only_an_incoming_call_flow_that_requires_the_forward_waits_for_it()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.InboundCall,
            Conditions = [new ForwardedOutsideCondition(true)],
            Steps = [new NotifyStep([FlowRecipient.ToOutside(OutsideScope.ThisCall)])],
        };

        Assert.True(Flows.WaitsForForward(flow));
        Assert.False(Flows.WaitsForForward(flow with { Conditions = [new ForwardedOutsideCondition(false)] }));
        Assert.False(Flows.WaitsForForward(flow with { Conditions = [] }));
        // A missed call is raised when it has ended, by when Talk has said where it went.
        Assert.False(Flows.WaitsForForward(flow with { Trigger = AlertEventType.MissedCall }));
    }

    [Fact]
    public void The_outside_recipient_and_condition_are_stored_and_read_back_and_checked()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Conditions = [new ForwardedOutsideCondition(true)],
            Steps = [new NotifyStep([FlowRecipient.ToOutside(OutsideScope.AnyLinked)], Urgent: true)],
        };

        var read = Flows.Read(Flows.Write(flow))!;
        Assert.Equal(new ForwardedOutsideCondition(true), Assert.Single(read.Conditions));
        var to = Assert.Single(Assert.IsType<NotifyStep>(Assert.Single(read.Steps)).To);
        Assert.Equal(OutsideScope.AnyLinked, to.Outside);
        Assert.True(to.IsOne);
        Assert.False(new FlowRecipient(Rang: true, Outside: OutsideScope.ThisCall).IsOne);
        Assert.Null(Flows.Problem(flow));
        Assert.NotNull(Flows.Problem(flow with { Trigger = AlertEventType.Drift, Steps = [new NotifyStep([FlowRecipient.ToPerson(Guid.NewGuid())])] }));
    }
}

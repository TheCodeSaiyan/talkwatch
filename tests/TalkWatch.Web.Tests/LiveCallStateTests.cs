using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Web.Components.Calls;

namespace TalkWatch.Web.Tests;

/// <summary>What a call on the Now board and Operator is doing, from the last step Talk reported.</summary>
public sealed class LiveCallStateTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddMinutes(-2);
    private static readonly ChronicleNames Names = new(_ => null, _ => null, _ => null, _ => null);

    private static LiveCall Of(params (string Event, string? Data)[] events)
    {
        var row = new CallRow { Id = Guid.NewGuid(), TalkUuid = "c1", Direction = "in", Status = "accepted", Time = T0, Outcome = CallOutcome.Answered, FromRaw = "+447700900774" };
        return LiveCalls.Of(row, Chronicle.Build(events.Select((e, i) => new ChronicleEvent(T0.AddSeconds(i * 10), e.Event, e.Data)), "in", Names), Names);
    }

    // Talk 5.4.5 logs a park and its pick-up; a parked caller is waiting, not talking to anyone.
    [Fact]
    public void A_parked_call_is_parked_until_someone_picks_it_up()
    {
        (string, string?)[] answered = [("call_started", "{}"), ("seq_call_trying_endpoints", "{}"), ("call_accepted", "{}")];

        var parked = Of([.. answered, ("call_parked", """{"parked_by_user_uuid":"u1"}""")]);
        var picked = Of([.. answered, ("call_parked", "{}"), ("call_unparked", "{}")]);

        Assert.Equal(("parked", "Parked", "parked"), (parked.State, LiveCalls.Words(parked.State), LiveCalls.Mark(parked.State)));
        Assert.Equal("connected", picked.State);
        Assert.Equal(T0.AddSeconds(40), picked.Since);
    }
}

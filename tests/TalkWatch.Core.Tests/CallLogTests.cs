using System.Text.Json;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Core.Tests;

public class CallLogTests
{
    private static readonly NumberNormaliser Uk = new("GB");
    private static readonly string[] Directions = ["in", "out", "internal"];
    private static readonly IReadOnlyList<CallLogRecord> Calls = new FixtureConsole(FixtureConsole.DefaultDirectory).Calls();

    [Fact]
    public void Every_captured_call_log_page_reads_as_talkwatch_expects()
    {
        var files = Directory.GetFiles(FixtureConsole.DefaultDirectory, "*-http-proxy-talk-api-call-log.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var page = JsonSerializer.Deserialize<CallLogPage>(File.ReadAllBytes(file), TalkJson.Options)!;
            Assert.All(page.Records, r =>
            {
                Assert.False(string.IsNullOrEmpty(r.Uuid));
                Assert.NotEqual(default, r.Time);
                Assert.Contains(r.Direction, Directions);
            });
        }
    }

    [Fact]
    public void An_inbound_call_through_the_switchboard_touches_its_number_attendant_and_contacts()
    {
        var call = Calls.Single(c => c.Uuid == "c8d59f53-0744-4ec3-aa39-4dba46b51135");

        var lines = CallRouting.TouchedLines(call, Uk);

        Assert.Equal(
            new HashSet<LineRef>
            {
                new(LineKind.Did, "+441144960042"),
                new(LineKind.Attendant, "45"),
                new(LineKind.Contact, "ac7a4901-2722-4e41-9f20-87f95df72cb2"),
                new(LineKind.Contact, "3"),
            },
            lines);
    }

    [Fact]
    public void A_call_sent_to_voicemail_touches_the_voicemail_owners_line()
    {
        // As a real console sent it: through the switchboard menu to a user's voicemail, where a message was left.
        var call = System.Text.Json.JsonSerializer.Deserialize<CallLogRecord>("""
            {"uuid": "b1c2", "time": "2026-09-30T16:57:17Z", "direction": "in", "status": "accepted",
             "from": "+447700900123", "to": "+441144960042", "to_smart_attendant_id": "15",
             "call_events": [
               {"time": "2026-09-30T16:57:17Z", "event": "call_started", "event_data": {"to_smart_attendant_id": 15}},
               {"time": "2026-09-30T16:57:57Z", "event": "call_sent_to_voicemail", "event_data": {"recipient_user_uuids": ["abe3a229-7539-48dd-a8e3-e23fd4d9f72f"]}},
               {"time": "2026-09-30T16:58:18Z", "event": "vm_msg_recorded", "event_data": {"recipient_user_uuids": ["abe3a229-7539-48dd-a8e3-e23fd4d9f72f"]}},
               {"time": "2026-09-30T16:58:18Z", "event": "call_hangup", "event_data": {"hangup_cause": "normal_end"}}
             ]}
            """, TalkJson.Options)!;

        var lines = CallRouting.TouchedLines(call, Uk);

        Assert.Contains(new LineRef(LineKind.User, "abe3a229-7539-48dd-a8e3-e23fd4d9f72f"), lines);
        Assert.Contains(new LineRef(LineKind.Attendant, "15"), lines);
    }

    [Fact]
    public void An_outbound_call_touches_the_user_who_made_it()
    {
        var outbound = Calls.Where(c => c.Direction == "out" && c.FromId is not null).ToList();
        Assert.NotEmpty(outbound);

        Assert.All(outbound, call => Assert.Contains(new LineRef(LineKind.User, call.FromId!), CallRouting.TouchedLines(call, Uk)));
    }

    [Fact]
    public void Every_call_touches_at_least_one_line()
    {
        // A call no line covers could only ever be seen by an admin, so this would be a hole in the grants.
        Assert.All(Calls, call => Assert.NotEmpty(CallRouting.TouchedLines(call, Uk)));
    }

    [Theory]
    [InlineData("+441144960042", "+441144960042")]
    [InlineData("01144960042", "+441144960042")]
    [InlineData("07700 900123", "+447700900123")]
    [InlineData("+1 212 555 0142", "+12125550142")]
    [InlineData("1001", null)]
    [InlineData("anonymous", null)]
    [InlineData(null, null)]
    public void Numbers_normalise_to_e164_or_not_at_all(string? raw, string? expected) =>
        Assert.Equal(expected, Uk.ToE164(raw));

    [Theory]
    [InlineData("+447700900123", "GB")]
    [InlineData("+390612345678", "IT")]
    [InlineData(null, null)]
    [InlineData("1001", null)]
    public void A_number_says_which_country_it_is_from(string? e164, string? region) =>
        Assert.Equal(region, NumberNormaliser.RegionOf(e164));
}

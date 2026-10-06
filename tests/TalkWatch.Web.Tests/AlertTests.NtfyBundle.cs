using System.Text;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    // A bundle's title reads "2 × Missed call": the × isn't ASCII, which an HTTP header must be, so it is encoded as ntfy
    // decodes, as a single alert's title is. Sent as it was, .NET refused the request and the bundle never went.
    [Fact]
    public async Task A_bundles_title_to_ntfy_is_a_header_http_can_carry()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var phone = await h.AddChannelAsync("Phone", ChannelKind.Ntfy, "https://ntfy.test/desk");
        await h.AddFlowAsync("Gathered", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new BundleStep(10), new NotifyStep([FlowRecipient.ToChannel(phone)])] });

        foreach (var (uuid, minutesAgo, from) in new[] { ("bundled-first", 3, "+447700900111"), ("bundled-second", 2, "+447700900222") })
        {
            var time = LongAfterTheFixtures.AddMinutes(-minutesAgo).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            h.Console.AddCall($$$"""
                {"uuid": "{{{uuid}}}", "time": "{{{time}}}", "direction": "in", "status": "accepted", "duration": 30, "from": "{{{from}}}", "to": "+441144960042",
                 "call_events": [{"time": "{{{time}}}", "event": "call_started"}, {"time": "{{{time}}}", "event": "seq_call_trying_endpoints"}, {"time": "{{{time}}}", "event": "call_hangup"}]}
                """);
            await h.PollAsync();
        }

        h.Clock.Now += TimeSpan.FromMinutes(10);
        await h.DispatchAsync();
        await h.DispatchAsync();

        var title = Assert.Single(h.Receiver.Requests).Headers["Title"];
        Assert.All(title, c => Assert.InRange(c, ' ', '~'));
        Assert.StartsWith("2 × ", Encoding.UTF8.GetString(Convert.FromBase64String(title["=?UTF-8?B?".Length..^"?=".Length])), StringComparison.Ordinal);
    }
}

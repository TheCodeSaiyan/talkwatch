using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Tests;

/// <summary>A notify step sending the call's summary and what was said: to those allowed to read them, and only them.</summary>
public sealed partial class AlertTests
{
    [Fact]
    public async Task What_was_said_goes_with_the_alert_only_to_a_channel_whose_owner_may_read_it()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();

        var site = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/office");
        // Sees the call through a grant on its number, but is not allowed its transcripts.
        var vic = await h.AddViewerAsync("vic", $"Did:{new NumberNormaliser("GB").ToE164(missed.To)}");
        var vics = await h.AddChannelAsync("Vic's", ChannelKind.Webhook, "https://hooks.test/vic", owner: vic.ToString());
        await h.AddFlowAsync("With what was said", new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(site), FlowRecipient.ToChannel(vics)], Include: NotifyIncludes.Summary | NotifyIncludes.Transcript)],
        });

        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();
        // Talk's transcript arrives after the alert is raised and before it is sent: what goes is what there is by then.
        await h.DbAsync(async db =>
        {
            var call = await db.Calls.SingleAsync(c => c.TalkUuid == missed.Uuid, Ct);
            db.CallTranscripts.Add(new CallTranscript
            {
                Id = Guid.NewGuid(), SiteId = call.SiteId, CallId = call.Id, TalkId = "t-1", Summary = "Asked about an order", Lines = "[]",
                Text = "Hello, it's about order 1234, please ring me back.", CopiedAt = h.Clock.Now,
            });
            return await db.SaveChangesAsync(Ct);
        });
        await h.DispatchAsync();

        JsonElement Sent(string host) => JsonDocument.Parse(h.Receiver.Requests.Single(r => r.Uri.Host == "hooks.test" && r.Uri.AbsolutePath == host).Body).RootElement;
        var office = Sent("/office");
        var hers = Sent("/vic");
        Assert.Equal("Asked about an order", office.GetProperty("summary").GetString());
        Assert.Contains("order 1234", office.GetProperty("transcript").GetString(), StringComparison.Ordinal);
        // Vic hears of the call, as her grant allows, but not what was said.
        Assert.Equal(JsonValueKind.Null, hers.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Null, hers.GetProperty("transcript").ValueKind);
        Assert.Equal(missed.Uuid, hers.GetProperty("call").GetProperty("uuid").GetString());
    }

    [Fact]
    public void Sending_what_was_said_is_only_for_alerts_about_a_call()
    {
        var flow = new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(Guid.NewGuid())], Include: NotifyIncludes.Transcript)],
        };

        Assert.NotNull(Flows.Problem(flow));
        Assert.Null(Flows.Problem(flow with { Trigger = AlertEventType.MissedCall }));
    }

    // Someone whose only way to the call is a role on its number hears of it on their own channel, as a grant would let them.
    [Fact]
    public async Task A_channels_owner_with_a_role_on_the_calls_number_hears_of_the_call()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        var noa = await h.AddViewerAsync("noa");
        var did = new NumberNormaliser("GB").ToE164(missed.To)!;
        await h.DbAsync(async db =>
        {
            var viewer = await db.Roles.Where(r => r.Name == Roles.Viewer).Select(r => r.Id).SingleAsync(Ct);
            db.NumberRoles.Add(new NumberRole { Id = Guid.NewGuid(), SiteId = await db.Sites.Select(x => x.Id).SingleAsync(Ct), UserId = noa, Did = did, RoleId = viewer, CreatedAt = h.Clock.Now });
            return await db.SaveChangesAsync(Ct);
        });
        var hers = await h.AddChannelAsync("Noa's", ChannelKind.Webhook, "https://hooks.test/noa", owner: noa.ToString());
        await h.SendToAsync("To Noa", AlertEventType.MissedCall, [hers]);

        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        Assert.True(await h.DbAsync(db => db.AlertDeliveries.AnyAsync(d => d.ChannelId == hers, Ct)));
    }
}

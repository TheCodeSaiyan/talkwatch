using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    // A branch can go on what a voicemail says, offered where it can work: a flow that starts on a transcribed voicemail.
    [Fact]
    public async Task The_editor_offers_a_branch_on_what_it_says_only_for_a_flow_on_transcribed_voicemail()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var branch = new BranchStep(new TimeCondition(0b0011111, new TimeOnly(9, 0), new TimeOnly(17, 0), Outside: false), [new NotifyStep([FlowRecipient.ToRang()])], []);
        await h.AddFlowAsync("On a transcript", new FlowDefinition { Trigger = AlertEventType.VoicemailTranscribed, Steps = [branch] });
        await h.AddFlowAsync("On a missed call", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [branch] });
        var ids = await h.DbAsync(db => db.AlertFlows.ToDictionaryAsync(f => f.Name, f => f.Id, Ct));

        var transcribed = await h.Admin.GetStringAsync(new Uri($"/flows/{ids["On a transcript"]}", UriKind.Relative), Ct);
        var missed = await h.Admin.GetStringAsync(new Uri($"/flows/{ids["On a missed call"]}", UriKind.Relative), Ct);

        Assert.Contains(">what it says</option>", transcribed, StringComparison.Ordinal);
        Assert.DoesNotContain(">what it says</option>", missed, StringComparison.Ordinal);
    }
}

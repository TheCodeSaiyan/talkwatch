using System.Net;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    // Who returns a caller, chosen by hand on the list, as a flow's Assign step does; and back to nobody in particular.
    [Fact]
    public async Task A_missed_caller_is_assigned_by_hand_and_back_to_nobody_and_not_by_someone_who_cannot_see_them()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [desk]);
        var missed = await MissedJustNowAsync(h);
        var number = new NumberNormaliser("GB").ToE164(missed.From)!;
        var sam = await h.AddViewerAsync("sam");
        await h.AddViewerAsync("elsewhere", "Did:+441174960999");
        using var viewer = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(viewer, "elsewhere", ViewerPassword);

        var assigned = await PostAsync(h.Admin, "/callbacks", "/callbacks/assign", ("Number", number), ("Person", sam.ToString()));

        Assert.Contains("Assigned%20to%20sam", assigned.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(sam, await h.DbAsync(db => db.Calls.Where(c => c.TalkUuid == missed.Uuid).Select(c => c.CallBackAssignedTo).SingleAsync(Ct)));
        Assert.Contains($"data-assigned=\"{sam}\"", await PageAsync(h.Admin), StringComparison.Ordinal);
        Assert.Equal(1, await h.DbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "callback.assign", Ct)));

        // Someone who cannot see the caller cannot reassign them.
        var refused = await PostAsync(viewer, "/callbacks", "/callbacks/assign", ("Number", number), ("Person", ""));
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal(sam, await h.DbAsync(db => db.Calls.Where(c => c.TalkUuid == missed.Uuid).Select(c => c.CallBackAssignedTo).SingleAsync(Ct)));

        await PostAsync(h.Admin, "/callbacks", "/callbacks/assign", ("Number", number), ("Person", ""));

        Assert.Null(await h.DbAsync(db => db.Calls.Where(c => c.TalkUuid == missed.Uuid).Select(c => c.CallBackAssignedTo).SingleAsync(Ct)));
        Assert.DoesNotContain("data-assigned=", await PageAsync(h.Admin), StringComparison.Ordinal);
    }
}

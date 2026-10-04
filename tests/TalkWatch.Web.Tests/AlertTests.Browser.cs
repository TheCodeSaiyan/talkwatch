using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Core.Alerts;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests
{
    private const string PushEndpoint = "https://push.example.test/send/browser-1";

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>What a browser sends when it subscribes: its push service's address and a real P-256 key with a secret.</summary>
    private static object Subscription()
    {
        using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var q = key.ExportParameters(false).Q;
        return new { endpoint = PushEndpoint, keys = new { p256dh = Base64Url([0x04, .. q.X!, .. q.Y!]), auth = Base64Url(RandomNumberGenerator.GetBytes(16)) } };
    }

    [Fact]
    public async Task A_flow_reaches_a_persons_browser_the_bell_their_open_pages_and_a_desktop_notification()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);

        // The admin turns alerts in TalkWatch on and allows this browser.
        await PostAsync(h.Admin, "/account", "/account/browser-alerts");
        var subscribed = await h.Admin.PostAsJsonAsync(new Uri("/account/push", UriKind.Relative), Subscription(), Ct);
        await h.AddFlowAsync("Drift to me", new FlowDefinition { Trigger = AlertEventType.Drift, Steps = [new NotifyStep([FlowRecipient.ToPerson(admin)])] });
        var arrived = new List<Guid>();
        h.App.Services.GetRequiredService<BrowserAlerts>().Arrived += arrived.Add;

        await DriftAsync(h);

        Assert.Equal(HttpStatusCode.NoContent, subscribed.StatusCode);
        Assert.Equal([admin], arrived);
        var push = Assert.Single(h.Receiver.Requests);
        Assert.Equal(PushEndpoint, push.Uri.ToString());
        Assert.StartsWith("vapid t=", push.Headers["Authorization"], StringComparison.Ordinal);
        Assert.Equal("aes128gcm", push.Headers["Content-Encoding"]);
        Assert.Equal("high", push.Headers["Urgency"]);

        // Unread in the bell until the list is opened.
        Assert.Contains("data-bell=\"1\"", await h.Admin.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct), StringComparison.Ordinal);
        var inbox = await h.Admin.GetStringAsync(new Uri("/alerts/inbox", UriKind.Relative), Ct);
        Assert.Contains("TalkWatch stopped copying calls", inbox, StringComparison.Ordinal);
        Assert.Contains("data-bell=\"0\"", await h.Admin.GetStringAsync(new Uri("/calls", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_whose_push_service_says_it_is_gone_is_forgotten_and_the_alert_is_in_the_bell_all_the_same()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        await PostAsync(h.Admin, "/account", "/account/browser-alerts");
        await h.Admin.PostAsJsonAsync(new Uri("/account/push", UriKind.Relative), Subscription(), Ct);
        await h.AddFlowAsync("Drift to me", new FlowDefinition { Trigger = AlertEventType.Drift, Steps = [new NotifyStep([FlowRecipient.ToPerson(admin)])] });
        h.Receiver.Status = HttpStatusCode.Gone;

        await DriftAsync(h);

        Assert.Equal(0, await h.DbAsync(db => db.PushSubscriptions.CountAsync(Ct)));
        Assert.Equal(DeliveryState.Sent, await h.DbAsync(db => db.AlertDeliveries.Select(d => d.State).SingleAsync(Ct)));
        Assert.Contains("TalkWatch stopped copying calls", await h.Admin.GetStringAsync(new Uri("/alerts/inbox", UriKind.Relative), Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_viewers_browser_hears_only_of_their_own_lines_and_turning_it_off_removes_it()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var viewer = await h.AddViewerAsync("viewer", "Did:+441174960999");
        using var browser = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(browser, "viewer", ViewerPassword);
        await PostAsync(browser, "/account", "/account/browser-alerts");
        await h.AddFlowAsync("Missed to the viewer", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToPerson(viewer)])] });
        var channel = await h.DbAsync(db => db.AlertChannels.SingleAsync(c => c.Kind == ChannelKind.Browser, Ct));

        // A missed call on a line the viewer is not granted: their browser hears nothing of it.
        var (missed, newer) = NewestMissed(h.Console);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();
        await h.DispatchAsync();

        Assert.Equal(viewer, channel.OwnerUserId);
        Assert.Equal(0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.ChannelId == channel.Id, Ct)));
        Assert.Contains("data-inbox-empty", await browser.GetStringAsync(new Uri("/alerts/inbox", UriKind.Relative), Ct), StringComparison.Ordinal);

        await PostAsync(browser, "/account", "/account/browser-alerts");
        Assert.Equal(0, await h.DbAsync(db => db.AlertChannels.CountAsync(c => c.Kind == ChannelKind.Browser, Ct)));
    }
}

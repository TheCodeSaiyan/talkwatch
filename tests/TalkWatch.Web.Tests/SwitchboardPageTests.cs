using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed class SwitchboardPageTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>An inbound call to a switchboard: started, then <paramref name="middle"/> a second apart, then hung up after <paramref name="lasting"/>.</summary>
    private static string Call(string uuid, DateTimeOffset at, string from, int switchboard, TimeSpan lasting, params JsonObject[] middle)
    {
        JsonObject Event(DateTimeOffset when, string name, JsonObject data) => new() { ["time"] = At(when), ["event"] = name, ["event_data"] = data };
        var events = new JsonArray { Event(at, "call_started", new JsonObject { ["to"] = "+441144960042", ["to_smart_attendant_id"] = switchboard }) };
        for (var i = 0; i < middle.Length; i++)
        {
            events.Add(Event(at.AddSeconds(i + 1), (string)middle[i]["event"]!, (JsonObject)middle[i]["event_data"]!.DeepClone()));
        }

        events.Add(Event(at + lasting, "call_hangup", new JsonObject { ["hangup_cause"] = "normal_end" }));
        return new JsonObject
        {
            ["uuid"] = uuid, ["time"] = At(at), ["direction"] = "in", ["status"] = "accepted", ["duration"] = (int)lasting.TotalSeconds,
            ["from"] = from, ["to"] = "+441144960042", ["to_smart_attendant_id"] = switchboard, ["call_events"] = events,
        }.ToJsonString();
    }

    private static JsonObject Menu(int menu, int item, int key) => new()
    {
        ["event"] = "entered_sa_menu",
        ["event_data"] = new JsonObject { ["sa_id"] = menu, ["sa_item_id"] = item, ["sa_item_key"] = key, ["sa_item_type"] = "ivr", ["sa_item_title"] = "As it was called then" },
    };

    private static readonly JsonObject Rang = new() { ["event"] = "seq_call_trying_endpoints", ["event_data"] = new JsonObject() };
    private static readonly JsonObject Answered = new() { ["event"] = "call_accepted", ["event_data"] = new JsonObject() };

    [Fact]
    public async Task The_switchboard_page_shows_greeting_hang_ups_and_what_each_menu_option_led_to()
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        // The captured calls a year back, out of the period, so only these count.
        console.ShiftTimes(DateTimeOffset.UtcNow.AddDays(-365));
        var day = DateTimeOffset.UtcNow.AddDays(-1);
        // Switchboard 45, no menu: three hang-ups at 5 seconds from someone who keeps ringing, one at 40 from someone new.
        for (var i = 0; i < 3; i++)
        {
            console.AddCall(Call($"greeting-{i}", day.AddMinutes(i), "+447700900501", 45, TimeSpan.FromSeconds(5)));
        }

        console.AddCall(Call("greeting-new", day.AddMinutes(10), "+447700900502", 45, TimeSpan.FromSeconds(40)));
        // Switchboard 15's menu: 1 then 2, answered; 3, rang out.
        console.AddCall(Call("menu-1-2", day.AddMinutes(20), "+447700900503", 15, TimeSpan.FromSeconds(90), Menu(15, 16, 1), Menu(16, 42, 2), Rang, Answered));
        console.AddCall(Call("menu-3", day.AddMinutes(30), "+447700900504", 15, TimeSpan.FromSeconds(60), Menu(15, 19, 3), Rang));
        await using var app = talkwatch.Create(console);
        await app.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await app.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);
        using var browser = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(browser, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);

        var page = WebUtility.HtmlDecode(await browser.GetStringAsync(new Uri("/switchboard?days=7", UriKind.Relative), Ct));

        var stored = await StoredGreetingAsync(app, 45);
        Assert.NotNull(stored);
        Assert.Contains($"data-figure=\"greeting\"><strong>{stored:0} s</strong>", page, StringComparison.Ordinal);
        Assert.Contains("data-figure=\"hung-up-in-greeting\"><strong>4</strong> hung up during the greeting (100", page, StringComparison.Ordinal);
        Assert.Contains("data-figure=\"median-hang-up\"><strong>5 s</strong>", page, StringComparison.Ordinal);
        Assert.Contains("3 of them had rung before or rang again", page, StringComparison.Ordinal);
        Assert.Contains($"{(stored > 40 ? 4 : stored > 5 ? 3 : 0)} of the 4 hung up before the greeting had finished", page, StringComparison.Ordinal);

        // Options are named as the switchboard names them now, not as the event did then.
        var names = await NamesAsync(app);
        Assert.Contains($"<tr data-option=\"1 {names[16]} › 2 {names[42]}\"><td>1 {names[16]} › 2 {names[42]}</td><td>1</td><td>1</td><td>0</td><td>0</td><td>0</td></tr>", page, StringComparison.Ordinal);
        Assert.Contains($"<tr data-option=\"3 {names[19]}\"><td>3 {names[19]}</td><td>1</td><td>0</td><td>1</td><td>0</td><td>0</td></tr>", page, StringComparison.Ordinal);
    }

    private static async Task<double?> StoredGreetingAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, int switchboard)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().SwitchboardNodes
            .Where(n => n.InternalId == switchboard).Select(n => n.GreetingSeconds).SingleAsync(Ct);
    }

    private static async Task<Dictionary<int, string>> NamesAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().SwitchboardNodes
            .Where(n => n.InternalId != null).ToDictionaryAsync(n => n.InternalId!.Value, n => n.Title ?? "", Ct);
    }
}

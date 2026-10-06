using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Data;
using TalkWatch.Replay;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

public sealed partial class AlertTests(TalkWatchApp talkwatch) : IClassFixture<TalkWatchApp>
{
    private const string ViewerPassword = "a long enough password";
    private const string SigningSecret = "webhook-signing-secret";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset LongAfterTheFixtures = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    /// <summary>The alerting clock: moved by the test, and its timers never fire, so only the test runs the dispatcher.</summary>
    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Idle();

        private sealed class Idle : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Stands in for ntfy and webhook receivers: keeps what was sent and answers with <see cref="Status"/>.</summary>
    private sealed class Receiver : HttpMessageHandler
    {
        public sealed record Received(Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body);

        public List<Received> Requests { get; } = [];
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Received(request.RequestUri!, headers, body));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed record Harness(WebApplicationFactory<Program> App, HttpClient Admin, FixtureConsole Console, TestClock Clock, Receiver Receiver) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Admin.Dispose();
            Receiver.Dispose();
            await App.DisposeAsync();
        }

        public Task PollAsync() => App.Services.GetRequiredService<CallLogPoller>().RunOnceAsync(Ct);

        public Task<int> DispatchAsync() => App.Services.GetRequiredService<AlertDispatcher>().RunOnceAsync(Ct);

        public async Task<T> DbAsync<T>(Func<TalkWatchDbContext, Task<T>> work)
        {
            using var scope = App.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            return await work(scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>());
        }

        public async Task<Guid> AddChannelAsync(string name, ChannelKind kind, string target, string secret = "", string owner = "", params (string Name, string Value)[] more)
        {
            await PostAsync(Admin, "/admin/alerts", "/admin/alerts/channels",
                [("Name", name), ("Kind", kind.ToString()), ("Target", target), ("Secret", secret), ("Owner", owner), .. more]);
            return await DbAsync(db => db.AlertChannels.Where(c => c.Name == name).Select(c => c.Id).SingleAsync(Ct));
        }

        /// <summary>Saves a flow as the editor would, through the form endpoint; as the admin unless another browser is given.</summary>
        public async Task<HttpResponseMessage> AddFlowAsync(string name, FlowDefinition flow, HttpClient? browser = null) =>
            await PostAsync(browser ?? Admin, "/admin/alerts", "/admin/alerts/flows", ("Name", name), ("Definition", Flows.Write(flow)));

        /// <summary>The simplest flow: on this event, if these conditions hold, notify these channels.</summary>
        public Task<HttpResponseMessage> SendToAsync(string name, AlertEventType type, IEnumerable<Guid> channels, params FlowCondition[] conditions) =>
            AddFlowAsync(name, new FlowDefinition { Trigger = type, Conditions = conditions, Steps = [new NotifyStep([.. channels.Select(FlowRecipient.ToChannel)])] });

        public Task<Guid> UserIdAsync(string username) => DbAsync(db => db.Users.Where(u => u.UserName == username).Select(u => u.Id).SingleAsync(Ct));

        public async Task<Guid> AddViewerAsync(string username, string? grantedLine = null)
        {
            var created = await PostAsync(Admin, "/admin/users", "/admin/users", ("Username", username), ("Role", Roles.Viewer), ("Password", ViewerPassword));
            var id = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
            if (grantedLine is not null)
            {
                await PostAsync(Admin, $"/admin/users/{id}", $"/admin/users/{id}/grants", ("Line", grantedLine));
            }

            return id;
        }
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient browser, string page, string action, params (string Name, string Value)[] fields)
    {
        var html = await browser.GetStringAsync(new Uri(page, UriKind.Relative), Ct);
        var form = fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))
            .Append(new("__RequestVerificationToken", WebUtility.HtmlDecode(Token().Match(html).Groups[1].Value)));
        using var content = new FormUrlEncodedContent(form);
        return await browser.PostAsync(new Uri(action, UriKind.Relative), content);
    }

    private async Task<Harness> StartAsync(DateTimeOffset now, Action<IServiceCollection>? more = null)
    {
        var console = new FixtureConsole(FixtureConsole.DefaultDirectory);
        var clock = new TestClock(now);
        var receiver = new Receiver();
        var app = talkwatch.Create(console, services: s =>
        {
            s.AddSingleton(sp => ActivatorUtilities.CreateInstance<AlertService>(sp, (TimeProvider)clock));
            s.AddSingleton(sp => ActivatorUtilities.CreateInstance<AlertDispatcher>(sp, (TimeProvider)clock));
            s.AddSingleton(sp => ActivatorUtilities.CreateInstance<ConsoleHealthMonitor>(sp, (TimeProvider)clock));
            s.AddHttpClient(AlertDispatcher.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
            s.AddHttpClient(AlertDispatcher.PublicHttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
            s.AddHttpClient(WebPushSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => receiver);
            s.Configure<SiteOptions>(o => o.PublicUrl = new Uri("https://talkwatch.test"));
            more?.Invoke(s);
        });
        var admin = TalkWatchApp.Browser(app);
        await TalkWatchApp.SignInAsync(admin, TalkWatchApp.AdminUsername, TalkWatchApp.AdminPassword);
        return new Harness(app, admin, console, clock, receiver);
    }

    /// <summary>
    /// The newest inbound call Talk put through, served as missed, and how many calls are newer than it. The capture
    /// has no inbound call that was missed, so one is made from a real call.
    /// </summary>
    private static (CallLogRecord Call, int Newer) NewestMissed(FixtureConsole console)
    {
        var calls = console.Calls();
        var index = calls.ToList().FindIndex(c => c.Direction == "in" && c.Status == "accepted");
        console.Statuses[calls[index].Uuid] = "cancelled";
        return (calls[index], index);
    }

    [Fact]
    public async Task A_refused_channel_comes_back_to_its_form_saying_why_with_what_was_typed()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);

        // An ntfy channel with no address: refused.
        var refused = await PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/channels",
            ("Name", "Support phones"), ("Kind", "Ntfy"), ("Target", ""), ("Secret", "do-not-echo-this"), ("Owner", ""), ("QuietDays", "Saturday"), ("QuietStart", "20:00"), ("QuietEnd", "08:00"));
        var back = refused.Headers.Location!.OriginalString;
        var page = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri(back, UriKind.Relative), Ct));

        Assert.EndsWith("#add-channel", back, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-echo-this", back, StringComparison.Ordinal);
        // The reason is in the form's own panel, by the fields, not at the top of the page.
        var form = page[page.IndexOf("id=\"add-channel\"", StringComparison.Ordinal)..];
        Assert.Contains("data-channel-problem", form[..form.IndexOf("</form>", StringComparison.Ordinal)], StringComparison.Ordinal);
        Assert.Contains("Give an http or https address.", form, StringComparison.Ordinal);
        Assert.Contains("value=\"Support phones\"", form, StringComparison.Ordinal);
        Assert.Contains("value=\"Ntfy\" selected", form, StringComparison.Ordinal);
        Assert.Contains("value=\"Saturday\" checked", form, StringComparison.Ordinal);
        Assert.Contains("value=\"20:00\"", form, StringComparison.Ordinal);
        Assert.Equal(0, await h.DbAsync(db => db.AlertChannels.CountAsync(Ct)));
    }

    [Fact]
    public async Task Importing_history_raises_nothing()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        await h.SendToAsync("Every call", AlertEventType.InboundCall, [channel]);
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [channel]);

        await h.PollAsync();

        Assert.True(await h.DbAsync(db => db.Calls.AnyAsync(Ct)));
        Assert.Equal(0, await h.DbAsync(db => db.AlertEvents.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_recent_missed_call_is_sent_once_as_a_signed_webhook()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in", SigningSecret);
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [channel]);

        // History first, then the missed call arrives a couple of minutes before "now".
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();
        await h.PollAsync();

        var key = $"call:{missed.Uuid}:{AlertEventType.MissedCall}";
        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == key, Ct));
        Assert.Equal(1, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.EventId == alert.Id, Ct)));

        await h.DispatchAsync();
        await h.DispatchAsync();

        var sent = Assert.Single(h.Receiver.Requests, r => r.Headers.GetValueOrDefault("X-TalkWatch-Event") == nameof(AlertEventType.MissedCall)
            && Encoding.UTF8.GetString(r.Body).Contains(missed.Uuid, StringComparison.Ordinal));
        var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningSecret), sent.Body));
        Assert.Equal(expected, sent.Headers["X-TalkWatch-Signature"]);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("Missed call", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(missed.Uuid, body.RootElement.GetProperty("call").GetProperty("uuid").GetString());
        Assert.Equal(DeliveryState.Sent, await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.State).SingleAsync(Ct)));
        Assert.DoesNotContain(SigningSecret, await h.DbAsync(db => db.AlertChannels.Select(c => c.ProtectedSecret).SingleAsync(Ct)), StringComparison.Ordinal);
    }

    // Talk logs a forward to an outside Contact as answered when anything picks up. Ticked as an answering line, a call
    // its voicemail took is read from the transcript as outside voicemail, raises Voicemail late and once, keeps that
    // through a re-read from Talk, and a person's correction stands over the transcript.
    [Fact]
    public async Task A_call_an_outside_answering_lines_voicemail_took_becomes_voicemail_and_stays_so()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        await h.SendToAsync("Voicemail", AlertEventType.Voicemail, [channel]);
        var call = await h.DbAsync(db => db.Calls.Include(c => c.Lines)
            .Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered && c.Lines.Any(l => l.Kind == Core.Calls.LineKind.Contact))
            .OrderByDescending(c => c.Time).FirstAsync(Ct));
        var contact = AnsweringLineCheck.ContactOf(call)!;
        await h.DbAsync(async db =>
        {
            db.AnsweringLines.Add(new AnsweringLine { SiteId = call.SiteId, ContactId = contact, Phrases = "is not available\nleave a message after the tone" });
            return await db.SaveChangesAsync(Ct);
        });
        var check = h.App.Services.GetRequiredService<AnsweringLineCheck>();
        var forever = TimeSpan.FromDays(36500);
        TranscriptLine L(string text) => new(null, text, null, null);

        var changed = await check.CheckAsync(call.Id, [L("The person you have called is not available, please leave a message after the tone."), L("Hi, it's Ellie, please ring me back.")], forever, Ct);
        await check.CheckAsync(call.Id, [L("The person you have called is not available, please leave a message after the tone."), L("Hi, it's Ellie, please ring me back.")], forever, Ct);

        Assert.True(changed);
        var (outcome, finding, voicemails) = await h.DbAsync(async db => (
            await db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct),
            await db.CallFindings.SingleAsync(f => f.CallId == call.Id, Ct),
            await db.AlertEvents.CountAsync(e => e.CallId == call.Id && e.Type == AlertEventType.Voicemail, Ct)));
        Assert.Equal(CallOutcome.OutsideVoicemail, outcome);
        Assert.Equal((OutsideFinding.MessageLeft, "leave a message after the tone", false, contact), (finding.Finding, finding.Phrase, finding.ByHand, finding.ContactId));
        Assert.Equal(1, voicemails);

        // Talk sends the call again, still "answered": the finding holds.
        h.Console.HideNewest = 0;
        await h.PollAsync();
        Assert.Equal(CallOutcome.OutsideVoicemail, await h.DbAsync(db => db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct)));

        // A person heard it and a person answered: that stands, and the transcript read again changes nothing.
        await check.MarkAsync(call.Id, OutsideFinding.Person, "admin", forever, Ct);
        await check.CheckAsync(call.Id, [L("The person you have called is not available, please leave a message after the tone."), L("Hi, it's Ellie, please ring me back.")], forever, Ct);
        var after = await h.DbAsync(async db => (await db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct), await db.CallFindings.SingleAsync(f => f.CallId == call.Id, Ct)));
        Assert.Equal((CallOutcome.Answered, OutsideFinding.Person, true, "admin"), (after.Item1, after.Item2.Finding, after.Item2.ByHand, after.Item2.DecidedBy));
    }

    // Outside voicemail is found from the Configure menu, and from a forwarded call an outside number answered.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
    private static readonly string[] LeftAMessage = ["Sorry, the person you called is not available.", "Hi, it's Ellie, please call me back today."];
    private static readonly string[] PersonAnswered = ["Hello, Morgan speaking.", "Hi Morgan, it's about the delivery."];

    // A call's contact lines name the one that answered by its id, and contacts its forwarding could reach by their uuid:
    // only the id is the one that took it, whichever line comes first.
    [Fact]
    public void The_contact_that_answered_is_the_one_named_by_its_id()
    {
        var call = new CallRow { Id = Guid.NewGuid(), TalkUuid = "x", Direction = "in", Status = "accepted" };
        call.Lines.Add(new CallLine { CallId = call.Id, Kind = Core.Calls.LineKind.Contact, Key = "ac7a4901-2722-4e41-9f20-87f95df72cb2" });
        call.Lines.Add(new CallLine { CallId = call.Id, Kind = Core.Calls.LineKind.Contact, Key = "3" });

        Assert.Equal("3", AnsweringLineCheck.ContactOf(call));
        Assert.Null(AnsweringLineCheck.AnsweredBy(["ac7a4901-2722-4e41-9f20-87f95df72cb2"]));
    }

    // Phrases saved after calls were answered: reprocessing reads them all again, both ways, leaving corrections alone.
    [Fact]
    public async Task Reprocessing_reads_a_contacts_past_calls_again_against_its_phrases_as_they_are_now()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var keys = await h.DbAsync(db => db.CallLines.Where(l => l.Kind == Core.Calls.LineKind.Contact)
            .GroupBy(l => l.Key).OrderByDescending(g => g.Count()).Select(g => g.Key).ToListAsync(Ct));
        var contact = AnsweringLineCheck.AnsweredBy(keys)!;
        var calls = await h.DbAsync(db => db.Calls.Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered
                && c.Lines.Any(l => l.Kind == Core.Calls.LineKind.Contact && l.Key == contact) && !db.CallTranscripts.Any(t => t.CallId == c.Id))
            .OrderBy(c => c.Time).Take(2).ToListAsync(Ct));
        Assert.Equal(2, calls.Count);
        var (left, person) = (calls[0], calls[1]);
        // Set by hand, so it is passed over before its transcript, if any, is even read.
        var corrected = await h.DbAsync(db => db.Calls.Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered && c.Id != left.Id && c.Id != person.Id
            && c.Lines.Any(l => l.Kind == Core.Calls.LineKind.Contact && l.Key == contact)).FirstAsync(Ct));
        static string Lines(string[] said) => JsonSerializer.Serialize(said.Select(t => new TranscriptLine(null, t, null, null)), WebJson);
        await h.DbAsync(async db =>
        {
            foreach (var (call, said) in new (CallRow, string[])[] { (left, LeftAMessage), (person, PersonAnswered) })
            {
                db.CallTranscripts.Add(new CallTranscript { Id = Guid.NewGuid(), SiteId = call.SiteId, CallId = call.Id, TalkId = Guid.NewGuid().ToString("N"), Lines = Lines(said), Text = string.Join(' ', said) });
            }

            return await db.SaveChangesAsync(Ct);
        });
        await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines", ("ContactId", contact), ("Ticked", "true"), ("Phrases", "is not available"));
        await h.App.Services.GetRequiredService<AnsweringLineCheck>().MarkAsync(corrected.Id, OutsideFinding.Person, "admin", TimeSpan.FromDays(1), Ct);
        Task<CallOutcome> OutcomeOf(CallRow call) => h.DbAsync(db => db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct));

        var done = await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines/reprocess", ("ContactId", contact));

        Assert.Equal((CallOutcome.OutsideVoicemail, CallOutcome.Answered, CallOutcome.Answered), (await OutcomeOf(left), await OutcomeOf(person), await OutcomeOf(corrected)));
        var said = Uri.UnescapeDataString(done.Headers.Location!.OriginalString);
        Assert.Contains("1 left a message", said, StringComparison.Ordinal);
        Assert.Contains("reached a person", said, StringComparison.Ordinal);
        Assert.Contains("1 set by hand, left alone", said, StringComparison.Ordinal);
        // Old calls: reclassified, but no alert raised for them.
        Assert.Equal(0, await h.DbAsync(db => db.AlertEvents.CountAsync(e => e.CallId == left.Id, Ct)));

        // The phrase changed to one this greeting doesn't have: the call is answered again; the correction still stands.
        await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines", ("ContactId", contact), ("Ticked", "true"), ("Phrases", "leave your message after the beep"));
        await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines/reprocess", ("ContactId", contact));

        Assert.Equal((CallOutcome.Answered, CallOutcome.Answered), (await OutcomeOf(left), await OutcomeOf(corrected)));
        Assert.True(await h.DbAsync(db => db.CallFindings.Where(f => f.CallId == corrected.Id).Select(f => f.ByHand).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Outside_voicemail_is_in_the_configure_menu_and_offered_on_a_forwarded_call()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var call = await h.DbAsync(db => db.Calls.Include(c => c.Lines)
            .Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered && c.Lines.Any(l => l.Kind == Core.Calls.LineKind.Contact))
            .OrderByDescending(c => c.Time).FirstAsync(Ct));
        var contact = AnsweringLineCheck.ContactOf(call)!;

        var page = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri($"/calls/{call.TalkUuid}", UriKind.Relative), Ct));

        Assert.Contains("<a role=\"menuitem\" href=\"/admin/answering-lines\">Outside voicemail</a>", page, StringComparison.Ordinal);
        Assert.Contains("data-outside-unchecked", page, StringComparison.Ordinal);
        Assert.Contains($"href=\"/admin/answering-lines?open={contact}#contact-{contact}\"", page, StringComparison.Ordinal);
        var settings = await h.Admin.GetStringAsync(new Uri($"/admin/answering-lines?open={contact}", UriKind.Relative), Ct);
        Assert.Contains($"id=\"contact-{contact}\" open", settings, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_answering_line_is_ticked_with_its_phrases_shown_on_the_call_and_corrected_there()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var call = await h.DbAsync(db => db.Calls.Include(c => c.Lines)
            .Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered && c.Lines.Any(l => l.Kind == Core.Calls.LineKind.Contact)
                && !db.CallTranscripts.Any(t => t.CallId == c.Id))
            .OrderByDescending(c => c.Time).FirstAsync(Ct));
        var contact = AnsweringLineCheck.ContactOf(call)!;

        // Ticked with no phrases: refused. With phrases: kept, tidied to one a line.
        await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines", ("ContactId", contact), ("Ticked", "true"), ("Phrases", "  "));
        Assert.Equal(0, await h.DbAsync(db => db.AnsweringLines.CountAsync(Ct)));
        await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines", ("ContactId", contact), ("Ticked", "true"), ("Phrases", " is not available \r\n\r\nleave a message after the tone"));
        Assert.Equal("is not available\nleave a message after the tone", await h.DbAsync(db => db.AnsweringLines.Select(a => a.Phrases).SingleAsync(Ct)));
        Assert.Contains($"data-answering-line=\"{contact}\" data-ticked=\"yes\"", await h.Admin.GetStringAsync(new Uri("/admin/answering-lines", UriKind.Relative), Ct), StringComparison.Ordinal);

        // The call: not checked, as it has no transcript.
        var page = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri($"/calls/{call.TalkUuid}", UriKind.Relative), Ct));
        Assert.Contains("data-answering=\"unchecked\"", page, StringComparison.Ordinal);
        Assert.Contains("Not checked: there is no transcript", page, StringComparison.Ordinal);

        // Corrected by hand: a message was left.
        await PostAsync(h.Admin, $"/calls/{call.TalkUuid}", $"/calls/{call.TalkUuid}/answering", ("Finding", "message"));
        page = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri($"/calls/{call.TalkUuid}", UriKind.Relative), Ct));
        Assert.Equal(CallOutcome.OutsideVoicemail, await h.DbAsync(db => db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct)));
        Assert.Contains("Voicemail, outside", page, StringComparison.Ordinal);
        Assert.Contains("the caller left a message", page, StringComparison.Ordinal);
        Assert.Contains($"Set by {TalkWatchApp.AdminUsername}", page, StringComparison.Ordinal);
        Assert.Equal(1, await h.DbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "call.answering", Ct)));

        // Unticked: gone; the correction stays with the call.
        await PostAsync(h.Admin, "/admin/answering-lines", "/admin/alerts/answering-lines", ("ContactId", contact), ("Phrases", "is not available"));
        Assert.Equal(0, await h.DbAsync(db => db.AnsweringLines.CountAsync(Ct)));
        Assert.Equal(CallOutcome.OutsideVoicemail, await h.DbAsync(db => db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_call_to_a_contact_not_ticked_or_a_person_answering_is_left_answered()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var call = await h.DbAsync(db => db.Calls.Include(c => c.Lines)
            .Where(c => c.Direction == "in" && c.Outcome == CallOutcome.Answered && c.Lines.Any(l => l.Kind == Core.Calls.LineKind.Contact))
            .OrderByDescending(c => c.Time).FirstAsync(Ct));
        var check = h.App.Services.GetRequiredService<AnsweringLineCheck>();
        TranscriptLine[] greeting = [new(null, "The person you have called is not available, please leave a message after the tone.", null, null)];

        // Not ticked: nothing is read.
        Assert.False(await check.CheckAsync(call.Id, greeting, TimeSpan.FromDays(36500), Ct));

        await h.DbAsync(async db =>
        {
            db.AnsweringLines.Add(new AnsweringLine { SiteId = call.SiteId, ContactId = AnsweringLineCheck.ContactOf(call)!, Phrases = "leave a message after the tone" });
            return await db.SaveChangesAsync(Ct);
        });
        Assert.False(await check.CheckAsync(call.Id, [new(null, "Hello, Morgan speaking, how can I help you today?", null, null)], TimeSpan.FromDays(36500), Ct));

        Assert.Equal(CallOutcome.Answered, await h.DbAsync(db => db.Calls.Where(c => c.Id == call.Id).Select(c => c.Outcome).SingleAsync(Ct)));
        Assert.Equal(OutsideFinding.Person, await h.DbAsync(db => db.CallFindings.Where(f => f.CallId == call.Id).Select(f => f.Finding).SingleAsync(Ct)));
    }

    [Fact]
    public async Task A_missed_call_on_ntfy_names_the_caller_opens_the_call_and_offers_to_ring_back()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var channel = await h.AddChannelAsync("Phone", ChannelKind.Ntfy, "https://ntfy.test/talkwatch");
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [channel]);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        await h.DispatchAsync();

        var call = await h.DbAsync(db => db.Calls.SingleAsync(c => c.TalkUuid == missed.Uuid, Ct));
        var sent = Assert.Single(h.Receiver.Requests, r => r.Uri.ToString() == "https://ntfy.test/talkwatch");
        var who = call.CallerName ?? TalkWatch.Core.Calls.NumberNormaliser.Display(call.FromE164) ?? "a withheld number";
        Assert.Equal(NtfyText.Header($"Missed call from {who}"), sent.Headers["Title"]);
        Assert.EndsWith($"/calls/{call.TalkUuid}", sent.Headers["Click"], StringComparison.Ordinal);
        var body = Encoding.UTF8.GetString(sent.Body);
        Assert.Contains(TimeZoneInfo.ConvertTime(call.Time, h.App.Services.GetRequiredService<AlertService>().Zone).ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.EndsWith("\nFlow: Missed", body, StringComparison.Ordinal);
        var actions = sent.Headers["Actions"];
        Assert.StartsWith("http, Acknowledge, ", actions, StringComparison.Ordinal);
        Assert.EndsWith(", Open, " + actions.Split(", ")[2].Replace("/ack", "", StringComparison.Ordinal), actions, StringComparison.Ordinal);
        if (call.FromE164 is { } number)
        {
            Assert.Contains($"view, Call back, tel:{number}", actions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_call_that_rang_out_unanswered_is_a_missed_call_although_talk_marks_it_accepted()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var calls = h.Console.Calls();
        var index = calls.ToList().FindIndex(c => c.Direction == "in" && c.Status == "accepted"
            && c.CallEvents.Any(e => e.Event == "seq_call_trying_endpoints") && c.CallEvents.Any(e => e.Event == "call_hangup")
            && !c.CallEvents.Any(e => e.Event is "call_accepted" or "call_sent_to_voicemail"));
        Assert.True(index >= 0, "The fixtures have no inbound call that rang out unanswered.");
        var rangOut = calls[index];
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [channel]);

        h.Console.HideNewest = index + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = rangOut.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        Assert.True(await h.DbAsync(db => db.AlertEvents.AnyAsync(e => e.Key == $"call:{rangOut.Uuid}:{AlertEventType.MissedCall}", Ct)));
    }

    [Theory]
    [InlineData("Only", true)]
    [InlineData("Except", false)]
    public async Task A_caller_condition_picks_the_calls_a_flow_is_about(string mode, bool alerted)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        // The caller as a person would type it: national format, which the flow stores normalised.
        await h.SendToAsync("Missed", AlertEventType.MissedCall, [channel], new CallerFlowCondition(Enum.Parse<CallerMode>(mode), [missed.From!]));

        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:{missed.Uuid}:{AlertEventType.MissedCall}", Ct));
        Assert.Equal(alerted ? 1 : 0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.EventId == alert.Id, Ct)));
    }

    [Fact]
    public async Task Whoever_is_free_in_a_ring_group_reaches_the_people_linked_to_its_members()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        var (missed, newer) = NewestMissed(h.Console);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();

        var group = h.App.Services.GetRequiredService<LineDirectorySync>().Current.Groups.First(g => g.MemberList is { Count: > 0 });
        var member = group.MemberList![0];
        var outsider = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.User && !group.MemberList.Contains(l.Key)).Select(l => l.Key).FirstAsync(Ct));
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        var created = await PostAsync(h.Admin, "/admin/users", "/admin/users", ("Username", "elsewhere"), ("Role", Roles.Admin), ("Password", ViewerPassword));
        var elsewhere = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        await PostAsync(h.Admin, $"/admin/users/{admin}", $"/admin/users/{admin}/talk-user", ("TalkUser", member));
        await PostAsync(h.Admin, $"/admin/users/{elsewhere}", $"/admin/users/{elsewhere}/talk-user", ("TalkUser", outsider));
        var mine = await h.AddChannelAsync("Mine", ChannelKind.Webhook, "https://hooks.test/mine", owner: admin.ToString());
        await h.AddChannelAsync("Theirs", ChannelKind.Webhook, "https://hooks.test/theirs", owner: elsewhere.ToString());
        await h.AddFlowAsync("Whoever is free", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToFreeIn(group.Id)])] });

        // The live feed is not connected here, so nobody is known to be free and every linked member is told.
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:{missed.Uuid}:{AlertEventType.MissedCall}", Ct));
        Assert.Equal([mine], await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.ChannelId).ToListAsync(Ct)));
    }

    [Fact]
    public async Task With_the_live_feed_connected_only_the_free_members_of_a_ring_group_are_told()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();

        // The captured console's groups have one member each, so the console answers with a group of two.
        var users = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.User).OrderBy(l => l.Key).Select(l => l.Key).Take(2).ToListAsync(Ct));
        var (free, talking) = (users[0], users[1]);
        h.Console.Overrides["/proxy/talk/api/group_list"] = $$$"""
            [{"id": 1, "uuid": "group-of-two", "name": "Front desk", "member_list": ["{{{free}}}", "{{{talking}}}"], "did_list": [], "ext_list": ["0004"], "group_type": "ring_group"}]
            """;
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);

        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        var created = await PostAsync(h.Admin, "/admin/users", "/admin/users", ("Username", "on-a-call"), ("Role", Roles.Admin), ("Password", ViewerPassword));
        var busy = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        await PostAsync(h.Admin, $"/admin/users/{admin}", $"/admin/users/{admin}/talk-user", ("TalkUser", free));
        await PostAsync(h.Admin, $"/admin/users/{busy}", $"/admin/users/{busy}/talk-user", ("TalkUser", talking));
        var mine = await h.AddChannelAsync("Mine", ChannelKind.Webhook, "https://hooks.test/mine", owner: admin.ToString());
        await h.AddChannelAsync("Theirs", ChannelKind.Webhook, "https://hooks.test/theirs", owner: busy.ToString());
        await h.AddFlowAsync("Whoever is free", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToFreeIn("1")])] });
        var live = h.App.Services.GetRequiredService<LiveStatus>();
        live.SetDirectory([new TalkUser { Uuid = free }, new TalkUser { Uuid = talking, HasActiveCalls = true }]);
        live.SetConnected(true);

        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:{missed.Uuid}:{AlertEventType.MissedCall}", Ct));
        Assert.Equal([mine], await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.ChannelId).ToListAsync(Ct)));
    }

    [Theory]
    [InlineData(true, "free", "free")]                  // One free, one on a call: only the free one.
    [InlineData(true, "", "free,talking")]              // Nobody free: everyone, so somebody hears of it.
    [InlineData(false, "free", "free,talking")]         // No live feed: nobody is known to be free, so everyone.
    public void Whoever_is_free_means_the_members_not_on_a_call_or_away(bool connected, string available, string reached)
    {
        string[] members = ["free", "talking"];
        var users = new[]
        {
            new LiveUser("free", "Free", "101", available.Length > 0 ? "available" : "dnd", OnCall: false),
            new LiveUser("talking", "Talking", "102", "available", OnCall: true),
            new LiveUser("not-in-the-group", "Elsewhere", "103", "available", OnCall: false),
        };

        Assert.Equal(reached.Split(','), AlertService.Reach(members, users, connected));
    }

    [Fact]
    public async Task Whoever_it_rang_reaches_the_people_linked_to_the_lines_the_call_rang()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var users = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.User).OrderBy(l => l.Key).Select(l => l.Key).Take(2).ToListAsync(Ct));
        var (rung, outsider) = (users[0], users[1]);
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        var created = await PostAsync(h.Admin, "/admin/users", "/admin/users", ("Username", "not-rung"), ("Role", Roles.Admin), ("Password", ViewerPassword));
        var notRung = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        await PostAsync(h.Admin, $"/admin/users/{admin}", $"/admin/users/{admin}/talk-user", ("TalkUser", rung));
        await PostAsync(h.Admin, $"/admin/users/{notRung}", $"/admin/users/{notRung}/talk-user", ("TalkUser", outsider));
        var mine = await h.AddChannelAsync("Mine", ChannelKind.Webhook, "https://hooks.test/mine", owner: admin.ToString());
        await h.AddChannelAsync("Theirs", ChannelKind.Webhook, "https://hooks.test/theirs", owner: notRung.ToString());
        await h.AddFlowAsync("Whoever it rang", new FlowDefinition { Trigger = AlertEventType.Voicemail, Steps = [new NotifyStep([FlowRecipient.ToRang()])] });

        // A call that rang out to the first person's voicemail just now: Talk names them as the call's recipient.
        var time = LongAfterTheFixtures.AddMinutes(-2).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        h.Console.AddCall($$$"""
            {"uuid": "rang-to-voicemail", "time": "{{{time}}}", "direction": "in", "status": "accepted", "duration": 40, "from": "+447700900123", "to": "+441144960042",
             "call_events": [
               {"time": "{{{time}}}", "event": "call_started"},
               {"time": "{{{time}}}", "event": "seq_call_trying_endpoints"},
               {"time": "{{{time}}}", "event": "call_sent_to_voicemail", "event_data": {"recipient_user_uuids": ["{{{rung}}}"]}},
               {"time": "{{{time}}}", "event": "vm_msg_recorded", "event_data": {"recipient_user_uuids": ["{{{rung}}}"]}},
               {"time": "{{{time}}}", "event": "call_hangup"}]}
            """);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:rang-to-voicemail:{AlertEventType.Voicemail}", Ct));
        Assert.Equal([mine], await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.ChannelId).ToListAsync(Ct)));
    }

    [Fact]
    public async Task Whoever_it_rang_includes_the_members_of_a_ring_group_a_missed_call_rang()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.App.Services.GetRequiredService<LineDirectorySync>().RefreshAsync(Ct);
        await h.PollAsync();
        var group = h.App.Services.GetRequiredService<LineDirectorySync>().Current.Groups.First(g => g.MemberList is { Count: > 0 });
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        await PostAsync(h.Admin, $"/admin/users/{admin}", $"/admin/users/{admin}/talk-user", ("TalkUser", group.MemberList![0]));
        var mine = await h.AddChannelAsync("Mine", ChannelKind.Webhook, "https://hooks.test/mine", owner: admin.ToString());
        await h.AddFlowAsync("Whoever it rang", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToRang()])] });

        // Rang the group and nobody answered: Talk names the group, not its members.
        var time = LongAfterTheFixtures.AddMinutes(-2).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        h.Console.AddCall($$$"""
            {"uuid": "rang-the-group", "time": "{{{time}}}", "direction": "in", "status": "accepted", "duration": 30, "from": "+447700900123", "to": "+441144960042",
             "to_group_id": "{{{group.Id}}}",
             "call_events": [
               {"time": "{{{time}}}", "event": "call_started"},
               {"time": "{{{time}}}", "event": "seq_call_trying_endpoints"},
               {"time": "{{{time}}}", "event": "call_hangup"}]}
            """);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:rang-the-group:{AlertEventType.MissedCall}", Ct));
        Assert.Equal([mine], await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.ChannelId).ToListAsync(Ct)));
    }

    [Fact]
    public async Task A_flow_gives_the_call_back_to_someone_and_the_call_backs_page_says_who()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        await h.AddFlowAsync("Hand it over", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new AssignStep(admin)] });

        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        Assert.Equal(admin, await h.DbAsync(db => db.Calls.Where(c => c.TalkUuid == missed.Uuid).Select(c => c.CallBackAssignedTo).SingleAsync(Ct)));
        var page = await h.Admin.GetStringAsync(new Uri("/callbacks?all=true", UriKind.Relative), Ct);
        Assert.Contains($"data-assigned=\"{admin}\">Assigned to {TalkWatchApp.AdminUsername}", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manager_gives_call_backs_only_to_themselves()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var line = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.Did).Select(l => l.Key).FirstAsync(Ct));
        var (manager, managerId) = await ManagerAsync(h, $"Did:{line}");
        using var _ = manager;
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);

        var other = await h.AddFlowAsync("To the admin", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new AssignStep(admin)] }, manager);
        var self = await h.AddFlowAsync("To me", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new AssignStep(managerId)] }, manager);

        Assert.Contains("Notify%20yourself", other.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("Flow%20saved", self.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bundle_sends_what_a_flow_gathered_for_a_channel_as_one_message()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk", SigningSecret);
        await h.AddFlowAsync("Gathered", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new BundleStep(10), new NotifyStep([FlowRecipient.ToChannel(desk)])] });

        // Two callers missed a minute apart, just now.
        foreach (var (uuid, minutesAgo, from) in new[] { ("bundled-first", 3, "+447700900111"), ("bundled-second", 2, "+447700900222") })
        {
            var time = LongAfterTheFixtures.AddMinutes(-minutesAgo).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            h.Console.AddCall($$$"""
                {"uuid": "{{{uuid}}}", "time": "{{{time}}}", "direction": "in", "status": "accepted", "duration": 30, "from": "{{{from}}}", "to": "+441144960042",
                 "call_events": [{"time": "{{{time}}}", "event": "call_started"}, {"time": "{{{time}}}", "event": "seq_call_trying_endpoints"}, {"time": "{{{time}}}", "event": "call_hangup"}]}
                """);
            await h.PollAsync();
        }

        await h.DispatchAsync();
        Assert.Empty(h.Receiver.Requests);

        h.Clock.Now += TimeSpan.FromMinutes(10);
        await h.DispatchAsync();
        await h.DispatchAsync();

        var sent = Assert.Single(h.Receiver.Requests);
        Assert.Equal("Bundle", sent.Headers["X-TalkWatch-Event"]);
        Assert.Equal("sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningSecret), sent.Body)), sent.Headers["X-TalkWatch-Signature"]);
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal(["bundled-first", "bundled-second"], body.RootElement.GetProperty("alerts").EnumerateArray().Select(a => a.GetProperty("call").GetProperty("uuid").GetString()).Order());
        Assert.Equal(2, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.State == DeliveryState.Sent, Ct)));
    }

    [Theory]
    [InlineData(-1, true)] // A second under how long it rang: longer than that.
    [InlineData(1, false)] // A second over: not longer.
    public async Task Rang_longer_than_is_measured_from_the_calls_own_events(int offset, bool alerted)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var started = missed.CallEvents.First(e => e.Event == "call_started").Time;
        var ended = missed.CallEvents.Where(e => e.Event is "call_accepted" or "call_sent_to_voicemail" or "call_hangup").Min(e => e.Time);
        var rang = (int)Math.Floor((ended - started).TotalSeconds);
        Assert.True(rang > 2, "The fixtures' missed call rang too briefly to test against.");
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        await h.SendToAsync("Rang long", AlertEventType.MissedCall, [channel], new RangLongerCondition(rang + offset));

        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:{missed.Uuid}:{AlertEventType.MissedCall}", Ct));
        Assert.Equal(alerted ? 1 : 0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.EventId == alert.Id, Ct)));
    }

    [Fact]
    public async Task A_talk_user_is_linked_to_one_person_and_only_to_a_talk_user_there_is()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.PollAsync();
        var talkUser = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.User).Select(l => l.Key).FirstAsync(Ct));
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        var viewer = await h.AddViewerAsync("second-person");

        var linked = await PostAsync(h.Admin, $"/admin/users/{admin}", $"/admin/users/{admin}/talk-user", ("TalkUser", talkUser));
        var twice = await PostAsync(h.Admin, $"/admin/users/{viewer}", $"/admin/users/{viewer}/talk-user", ("TalkUser", talkUser));
        var madeUp = await PostAsync(h.Admin, $"/admin/users/{viewer}", $"/admin/users/{viewer}/talk-user", ("TalkUser", "not-a-talk-user"));

        Assert.Contains("Linked", linked.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("already%20linked", twice.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Contains("Choose%20one", madeUp.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal((talkUser, (string?)null), await h.DbAsync(async db => (
            (await db.Users.SingleAsync(u => u.Id == admin, Ct)).TalkUserUuid,
            (await db.Users.SingleAsync(u => u.Id == viewer, Ct)).TalkUserUuid)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeat_caller_condition_counts_the_callers_earlier_calls_as_stored(bool rangBefore)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        h.Console.HideNewest = newer + 1;
        await h.PollAsync();

        // However often the fixtures' caller rang in the hour before, the flow asks for one call more than that and this one.
        var region = await h.DbAsync(db => db.Sites.Select(x => x.DefaultRegion).SingleAsync(Ct));
        var caller = new Core.Calls.NumberNormaliser(region).ToE164(missed.From) ?? throw new InvalidOperationException("The fixtures' missed call has no caller number.");
        var before = await h.DbAsync(db => db.Calls.CountAsync(c => c.FromE164 == caller && c.Time > missed.Time - TimeSpan.FromHours(1) && c.Time < missed.Time, Ct));
        await h.SendToAsync("Rang again", AlertEventType.MissedCall, [channel], new RepeatCallerCondition(before + 2, 60));
        if (rangBefore)
        {
            await h.DbAsync(async db =>
            {
                var site = await db.Calls.Select(c => c.SiteId).FirstAsync(Ct);
                db.Calls.Add(new CallRow
                {
                    Id = Guid.NewGuid(), SiteId = site, TalkUuid = "rang-twenty-minutes-before", Time = missed.Time - TimeSpan.FromMinutes(20),
                    Direction = "in", Status = "cancelled", Outcome = Core.Calls.CallOutcome.Missed, FromRaw = missed.From, FromE164 = caller,
                    IngestedAt = missed.Time, UpdatedAt = missed.Time,
                });
                return await db.SaveChangesAsync(Ct);
            });
        }

        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:{missed.Uuid}:{AlertEventType.MissedCall}", Ct));
        Assert.Equal(rangBefore ? 1 : 0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(d => d.EventId == alert.Id, Ct)));
    }

    [Theory]
    [InlineData("Drift", "withheld")]              // TalkWatch itself has no caller
    [InlineData("MissedCall", "0800*")]           // a prefix must be international
    [InlineData("MissedCall", "")]                // a condition needs callers
    public async Task A_caller_condition_that_cannot_work_is_refused(string eventType, string numbers)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");

        var response = await h.SendToAsync("Flow", Enum.Parse<AlertEventType>(eventType), [channel],
            new CallerFlowCondition(CallerMode.Except, numbers.Length == 0 ? [] : [numbers]));

        Assert.Equal(0, await h.DbAsync(db => db.AlertFlows.CountAsync(Ct)));
        Assert.DoesNotContain("Flow%20saved", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    /// <summary>A Manager granted one line, signed in. Returns their browser and id.</summary>
    private static async Task<(HttpClient Browser, Guid Id)> ManagerAsync(Harness h, string line)
    {
        var created = await PostAsync(h.Admin, "/admin/users", "/admin/users", ("Username", "manager"), ("Role", Roles.Manager), ("Password", ViewerPassword));
        var id = Guid.Parse(created.Headers.Location!.OriginalString.Split('/').Last().Split('?')[0]);
        await PostAsync(h.Admin, $"/admin/users/{id}", $"/admin/users/{id}/grants", ("Line", line));
        var browser = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(browser, "manager", ViewerPassword);
        return (browser, id);
    }

    [Fact]
    public async Task A_manager_sets_up_alerts_for_their_own_line_and_hears_of_its_missed_calls()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (missed, newer) = NewestMissed(h.Console);
        var did = new Core.Calls.NumberNormaliser("GB").ToE164(missed.To)!;
        var adminChannel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/office");
        var (manager, managerId) = await ManagerAsync(h, $"Did:{did}");
        using var _ = manager;

        // The form names the admin as owner; the channel is the Manager's all the same.
        await PostAsync(manager, "/admin/alerts", "/admin/alerts/channels", ("Name", "Mine"), ("Kind", "Webhook"),
            ("Target", "https://hooks.test/mine"), ("Secret", ""), ("Owner", (await h.DbAsync(db => db.Users.Where(u => u.UserName == TalkWatchApp.AdminUsername).Select(u => u.Id).SingleAsync(Ct))).ToString()));
        var mine = await h.DbAsync(db => db.AlertChannels.SingleAsync(c => c.Name == "Mine", Ct));
        // Any of their lines: a channel of theirs hears only of those, whatever else the flow would match. And
        // themselves, which reaches the same channel: one delivery, not two.
        await h.AddFlowAsync("My missed calls", new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(mine.Id), FlowRecipient.ToPerson(managerId)])],
        }, manager);

        h.Console.HideNewest = newer + 1;
        await h.PollAsync();
        h.Console.HideNewest = 0;
        h.Clock.Now = missed.Time + TimeSpan.FromMinutes(2);
        await h.PollAsync();

        var flow = await h.DbAsync(db => db.AlertFlows.SingleAsync(f => f.Name == "My missed calls", Ct));
        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(e => e.Key == $"call:{missed.Uuid}:{AlertEventType.MissedCall}", Ct));
        Assert.Equal(managerId, mine.OwnerUserId);
        Assert.Equal(managerId, flow.OwnerUserId);
        Assert.Equal([mine.Id], await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.ChannelId).ToListAsync(Ct)));
        Assert.NotEqual(Guid.Empty, adminChannel);
    }

    [Fact]
    public async Task A_manager_cannot_see_or_use_anything_that_is_not_theirs()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var adminChannel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/office");
        await h.SendToAsync("Drift", AlertEventType.Drift, [adminChannel]);
        await h.PollAsync();
        h.Console.Drifted = true;
        await h.PollAsync();
        var adminAlert = await h.DbAsync(db => db.AlertEvents.Select(e => e.Id).SingleAsync(Ct));
        var lines = await h.DbAsync(db => db.Lines.Where(l => l.Kind == Core.Calls.LineKind.Did).Select(l => l.Key).Take(2).ToListAsync(Ct));
        var (manager, _) = await ManagerAsync(h, $"Did:{lines[0]}");
        using var __ = manager;
        await PostAsync(manager, "/admin/alerts", "/admin/alerts/channels", ("Name", "Mine"), ("Kind", "Webhook"), ("Target", "https://hooks.test/mine"), ("Secret", ""), ("Owner", ""));
        var mine = await h.DbAsync(db => db.AlertChannels.Where(c => c.Name == "Mine").Select(c => c.Id).SingleAsync(Ct));

        var page = await manager.GetStringAsync(new Uri("/admin/alerts", UriKind.Relative), Ct);
        var toggle = await PostAsync(manager, "/admin/alerts", $"/admin/alerts/channels/{adminChannel}/toggle");
        var settings = await PostAsync(manager, "/admin/alerts", "/admin/alerts/settings", ("TelegramChatId", "-100999"));
        var ack = await PostAsync(manager, "/admin/alerts", $"/admin/alerts/events/{adminAlert}/ack");
        await h.SendToAsync("Borrowed", AlertEventType.MissedCall, [mine, adminChannel]);
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        var flows = new (string Name, FlowDefinition Flow)[]
        {
            ("Borrowed", new() { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToChannel(mine), FlowRecipient.ToChannel(adminChannel)])] }),
            ("Not mine", new() { Trigger = AlertEventType.MissedCall, Conditions = [new LineCondition([new(Core.Calls.LineKind.Did, lines[1])])], Steps = [new NotifyStep([FlowRecipient.ToChannel(mine)])] }),
            ("System", new() { Trigger = AlertEventType.Drift, Steps = [new NotifyStep([FlowRecipient.ToChannel(mine)])] }),
            ("Someone else", new() { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToPerson(admin)])] }),
        };
        foreach (var (name, flow) in flows)
        {
            await h.AddFlowAsync(name, flow, manager);
        }

        var adminFlow = await h.DbAsync(db => db.AlertFlows.Where(f => f.Name == "Drift").Select(f => f.Id).SingleAsync(Ct));
        var changed = await PostAsync(manager, "/admin/alerts", "/admin/alerts/flows", ("Id", adminFlow.ToString()), ("Name", "Mine now"),
            ("Definition", Flows.Write(new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToChannel(mine)])] })));
        var removed = await PostAsync(manager, "/admin/alerts", $"/admin/alerts/flows/{adminFlow}/delete");

        Assert.Contains("Mine", page, StringComparison.Ordinal);
        Assert.DoesNotContain("hooks.test/office", page, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"SmtpHost\"", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, toggle.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, settings.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ack.StatusCode);
        Assert.True(await h.DbAsync(db => db.AlertChannels.Where(c => c.Id == adminChannel).Select(c => c.Enabled).SingleAsync(Ct)));
        Assert.Null(await h.DbAsync(db => db.AlertEvents.Where(e => e.Id == adminAlert).Select(e => e.AcknowledgedAt).SingleAsync(Ct)));
        Assert.Contains("not%20there", changed.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, removed.StatusCode);
        Assert.Equal(["Borrowed", "Drift"], await h.DbAsync(db => db.AlertFlows.OrderBy(f => f.Name).Select(f => f.Name).ToListAsync(Ct)));
        Assert.Null(await h.DbAsync(db => db.AlertFlows.Where(f => f.Name == "Borrowed").Select(f => f.OwnerUserId).SingleAsync(Ct)));
    }

    [Fact]
    public async Task The_test_button_sends_an_ntfy_push_with_the_token()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Phone", ChannelKind.Ntfy, "https://ntfy.test/talkwatch", "tk_access_token");

        var response = await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/channels/{channel}/test");

        var sent = Assert.Single(h.Receiver.Requests);
        Assert.Equal("https://ntfy.test/talkwatch", sent.Uri.ToString());
        Assert.Equal("TalkWatch test", sent.Headers["Title"]);
        Assert.Equal("Bearer tk_access_token", sent.Headers["Authorization"]);
        Assert.Contains("Phone", Encoding.UTF8.GetString(sent.Body), StringComparison.Ordinal);
        Assert.Contains("Test%20sent", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_handset_going_offline_reaches_site_channels_and_only_the_person_it_belongs_to()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var (owner, other) = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        var site = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/office");
        var theirs = await h.AddChannelAsync("Theirs", ChannelKind.Webhook, "https://hooks.test/theirs",
            owner: (await h.AddViewerAsync("owner", $"User:{owner}")).ToString());
        var someoneElses = await h.AddChannelAsync("Someone else's", ChannelKind.Webhook, "https://hooks.test/other",
            owner: (await h.AddViewerAsync("other", $"User:{other}")).ToString());
        await h.SendToAsync("Handsets", AlertEventType.HandsetOffline, [site, theirs, someoneElses]);
        var listener = h.App.Services.GetRequiredService<LiveListener>();

        LiveMessage Device(string status) => LiveMessage.Parse(
            $$"""{"event":"DEVICES_UPDATED","data":[{"mac":"aabbccddeeff","model":"UTP-G3","display_name":"Front desk","user_id":"{{owner}}","status":"{{status}}"}]}""")!;
        await listener.HandleAsync(Device("online"), Ct);
        await listener.HandleAsync(Device("offline"), Ct);
        await listener.HandleAsync(Device("offline"), Ct);

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(Ct));
        var to = await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == alert.Id).Select(d => d.ChannelId).ToListAsync(Ct));
        Assert.Equal(AlertEventType.HandsetOffline, alert.Type);
        Assert.Equivalent(new[] { site, theirs }, to);
    }

    [Fact]
    public async Task Drift_alerts_once_when_it_starts()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Office", ChannelKind.Webhook, "https://hooks.test/in");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);
        h.Console.Drifted = true;

        await h.PollAsync();
        h.Clock.Now += TimeSpan.FromHours(2);
        await h.PollAsync();

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(Ct));
        Assert.Equal(AlertEventType.Drift, alert.Type);
        Assert.Equal(1, await h.DbAsync(db => db.AlertDeliveries.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_failing_channel_is_retried_with_back_off_and_then_given_up()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Broken", ChannelKind.Webhook, "https://hooks.test/down");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);
        h.Receiver.Status = HttpStatusCode.BadGateway;
        h.Console.Drifted = true;
        await h.PollAsync();

        await h.DispatchAsync();
        await h.DispatchAsync();
        var first = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(Ct));
        Assert.Equal((DeliveryState.Pending, 1), (first.State, first.Attempts));
        Assert.Equal(h.Clock.Now + TimeSpan.FromMinutes(1), first.NextAttemptAt);
        Assert.Contains("502", first.LastError, StringComparison.Ordinal);

        for (var i = 1; i < AlertDispatcher.MaxAttempts; i++)
        {
            h.Clock.Now += TimeSpan.FromHours(6);
            await h.DispatchAsync();
        }

        var last = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(Ct));
        Assert.Equal((DeliveryState.Dead, AlertDispatcher.MaxAttempts), (last.State, last.Attempts));
        h.Clock.Now += TimeSpan.FromDays(1);
        await h.DispatchAsync();
        Assert.Equal(AlertDispatcher.MaxAttempts, h.Receiver.Requests.Count);
    }

    [Theory]
    [InlineData("2026-09-30T09:00:00Z", false)] // Wednesday 10:00 in London: office hours
    [InlineData("2026-09-30T19:00:00Z", true)]  // Wednesday 20:00
    [InlineData("2026-10-03T09:00:00Z", true)]  // Saturday morning
    public async Task An_after_hours_flow_fires_only_outside_office_hours(string at, bool alerted)
    {
        await using var h = await StartAsync(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
        var channel = await h.AddChannelAsync("On call", ChannelKind.Webhook, "https://hooks.test/in");
        await h.SendToAsync("After hours", AlertEventType.Drift, [channel],
            new TimeCondition(TimeWindow.ToMask(TimeWindow.Weekdays), new TimeOnly(9, 0), new TimeOnly(17, 30), Outside: true));
        h.Console.Drifted = true;

        await h.PollAsync();

        Assert.Equal(1, await h.DbAsync(db => db.AlertEvents.CountAsync(Ct)));
        Assert.Equal(alerted ? 1 : 0, await h.DbAsync(db => db.AlertDeliveries.CountAsync(Ct)));
    }

    [Fact]
    public async Task Someone_who_is_not_an_admin_cannot_see_or_change_alerts()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.AddViewerAsync("viewer");
        using var viewer = TalkWatchApp.Browser(h.App);
        await TalkWatchApp.SignInAsync(viewer, "viewer", ViewerPassword);

        var page = await viewer.GetAsync(new Uri("/admin/alerts", UriKind.Relative), Ct);
        await PostAsync(viewer, "/calls", "/admin/alerts/channels", ("Name", "Sneaky"), ("Kind", "Webhook"), ("Target", "https://hooks.test/x"));

        Assert.NotEqual(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(0, await h.DbAsync(db => db.AlertChannels.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_flow_reaches_people_on_their_own_channels_stage_by_stage_until_someone_acknowledges()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var admin = await h.UserIdAsync(TalkWatchApp.AdminUsername);
        var viewer = await h.AddViewerAsync("viewer");
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.AddChannelAsync("Admin phone", ChannelKind.Webhook, "https://hooks.test/admin", owner: admin.ToString());
        await h.AddChannelAsync("Viewer phone", ChannelKind.Webhook, "https://hooks.test/viewer", owner: viewer.ToString());
        await h.AddFlowAsync("Drift ladder", new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps =
            [
                new NotifyStep([FlowRecipient.ToChannel(desk)]),
                new WaitStep(10),
                // A Viewer hears only of what they may see, and drift is about no line: only the admin is reached.
                new NotifyStep([FlowRecipient.ToPerson(admin), FlowRecipient.ToPerson(viewer)], Urgent: true),
                new WaitStep(30),
                new NotifyStep([FlowRecipient.ToChannel(desk)]),
            ],
        });

        await DriftAsync(h);
        Assert.Equal(["https://hooks.test/desk"], h.Receiver.Requests.Select(r => r.Uri.ToString()));

        h.Clock.Now += TimeSpan.FromMinutes(10);
        await h.DispatchAsync();
        await h.DispatchAsync();
        Assert.Equal(["https://hooks.test/desk", "https://hooks.test/admin"], h.Receiver.Requests.Select(r => r.Uri.ToString()));
        using (var body = JsonDocument.Parse(h.Receiver.Requests[1].Body))
        {
            Assert.Equal((1, true), (body.RootElement.GetProperty("stage").GetInt32(), body.RootElement.GetProperty("urgent").GetBoolean()));
        }

        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(Ct));
        await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/events/{alert.Id}/ack");
        h.Clock.Now += TimeSpan.FromHours(1);
        await h.DispatchAsync();

        // No third stage: only word to the two channels it reached that it is in hand.
        Assert.Equal(
            ["https://hooks.test/desk", "https://hooks.test/admin", "https://hooks.test/admin", "https://hooks.test/desk"],
            h.Receiver.Requests.Select(r => r.Uri.ToString()).Take(2).Concat(h.Receiver.Requests.Skip(2).Select(r => r.Uri.ToString()).Order(StringComparer.Ordinal)));
        Assert.All(h.Receiver.Requests.Skip(2), r => Assert.Equal("Acknowledged", r.Headers["X-TalkWatch-Event"]));
        var run = await h.DbAsync(db => db.AlertFlowRuns.SingleAsync(Ct));
        Assert.Equal((FlowRunState.Stopped, 2), (run.State, run.NextStage));
    }

    [Theory]
    [InlineData("2026-09-30T09:00:00Z", "https://hooks.test/desk")]    // Wednesday 10:00 in London
    [InlineData("2026-10-03T09:00:00Z", "https://hooks.test/on-call")] // Saturday
    public async Task A_branch_sends_each_alert_down_the_path_its_time_picks(string at, string reached)
    {
        await using var h = await StartAsync(DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture));
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        var onCall = await h.AddChannelAsync("On call", ChannelKind.Webhook, "https://hooks.test/on-call");
        await h.AddFlowAsync("Drift by time", new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps =
            [
                new BranchStep(new TimeCondition(TimeWindow.ToMask(TimeWindow.Weekdays), new TimeOnly(9, 0), new TimeOnly(17, 30), Outside: false),
                    Then: [new NotifyStep([FlowRecipient.ToChannel(desk)])],
                    Otherwise: [new NotifyStep([FlowRecipient.ToChannel(onCall)])]),
            ],
        });

        await DriftAsync(h);

        Assert.Equal([reached], h.Receiver.Requests.Select(r => r.Uri.ToString()));
    }

    [Fact]
    public async Task A_flow_that_starts_with_a_wait_sends_only_if_nobody_has_acknowledged_by_then()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.AddFlowAsync("Give it five minutes", new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps = [new WaitStep(5), new NotifyStep([FlowRecipient.ToChannel(desk)])],
        });

        await DriftAsync(h);
        Assert.Empty(h.Receiver.Requests);

        h.Clock.Now += TimeSpan.FromMinutes(5);
        await h.DispatchAsync();
        await h.DispatchAsync();

        Assert.Single(h.Receiver.Requests);
        Assert.Equal(FlowRunState.Done, await h.DbAsync(db => db.AlertFlowRuns.Select(r => r.State).SingleAsync(Ct)));
    }

    [Fact]
    public async Task The_flows_page_says_how_often_each_flow_fired_this_week()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.AddFlowAsync("Drift", new FlowDefinition { Trigger = AlertEventType.Drift, Steps = [new NotifyStep([FlowRecipient.ToChannel(desk)])] });
        await h.AddFlowAsync("Quiet", new FlowDefinition { Trigger = AlertEventType.MissedCall, Steps = [new NotifyStep([FlowRecipient.ToChannel(desk)])] });
        await DriftAsync(h);
        var (drift, quiet) = await h.DbAsync(async db => (
            await db.AlertFlows.Where(f => f.Name == "Drift").Select(f => f.Id).SingleAsync(Ct),
            await db.AlertFlows.Where(f => f.Name == "Quiet").Select(f => f.Id).SingleAsync(Ct)));

        var page = await h.Admin.GetStringAsync(new Uri("/flows", UriKind.Relative), Ct);

        Assert.Matches($"data-flow=\"{drift}\"(?s:(?!data-flow=).)*?<span class=\"v num\">1</span>", page);
        Assert.Matches($"data-flow=\"{quiet}\"(?s:(?!data-flow=).)*?<span class=\"v num\">0</span>(?s:(?!data-flow=).)*?Not fired this week", page);
    }

    [Fact]
    public async Task Turning_a_flow_off_stops_the_alerts_it_has_started_at_their_next_step()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.AddFlowAsync("Drift", new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(desk)]), new WaitStep(10), new NotifyStep([FlowRecipient.ToChannel(desk)])],
        });
        await DriftAsync(h);
        var flow = await h.DbAsync(db => db.AlertFlows.Select(f => f.Id).SingleAsync(Ct));

        await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/flows/{flow}/toggle");
        h.Clock.Now += TimeSpan.FromMinutes(15);
        await h.DispatchAsync();

        Assert.Single(h.Receiver.Requests);
        Assert.Equal(FlowRunState.Stopped, await h.DbAsync(db => db.AlertFlowRuns.Select(r => r.State).SingleAsync(Ct)));
    }

    [Fact]
    public async Task The_flows_page_draws_each_flow_as_its_chain_and_the_editor_opens_on_it()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var viewer = await h.AddViewerAsync("nobody-reachable");
        var desk = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        await h.AddFlowAsync("Ladder", new FlowDefinition
        {
            Trigger = AlertEventType.MissedCall,
            Conditions = [new CallerFlowCondition(CallerMode.Except, ["withheld"])],
            Steps = [new NotifyStep([FlowRecipient.ToChannel(desk)]), new WaitStep(10), new NotifyStep([FlowRecipient.ToPerson(viewer)], Urgent: true)],
        });
        var flow = await h.DbAsync(db => db.AlertFlows.Select(f => f.Id).SingleAsync(Ct));

        // Flows have their own page; the editor answers at its old address too, for links already out there.
        var page = await h.Admin.GetStringAsync(new Uri("/flows", UriKind.Relative), Ct);
        var editor = await h.Admin.GetAsync(new Uri($"/flows/{flow}", UriKind.Relative), Ct);
        var fresh = await h.Admin.GetAsync(new Uri("/flows/new", UriKind.Relative), Ct);
        var old = await h.Admin.GetAsync(new Uri($"/admin/alerts/flows/{flow}", UriKind.Relative), Ct);

        Assert.Contains("data-flow-view", page, StringComparison.Ordinal);
        Assert.Contains("Missed call", page, StringComparison.Ordinal);
        Assert.Contains("not from withheld", page, StringComparison.Ordinal);
        Assert.Contains("10 minutes for someone to acknowledge", page, StringComparison.Ordinal);
        // Named, and flagged: a person with no channel turned on would hear nothing.
        Assert.Contains("nobody-reachable (no channel)", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, editor.StatusCode);
        Assert.Contains("data-step=\"wait\"", await editor.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, old.StatusCode);
    }

    [GeneratedRegex(@"/a/([^/,\s]+)/ack")]
    private static partial Regex AckLink();

    /// <summary>Raises drift, which needs no fixture call, and sends it.</summary>
    private static async Task DriftAsync(Harness h)
    {
        h.Console.Drifted = true;
        await h.PollAsync();
        await h.DispatchAsync();
    }

    [Fact]
    public async Task The_link_in_a_notification_acknowledges_the_alert_without_signing_in()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Phone", ChannelKind.Ntfy, "https://ntfy.test/talkwatch");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);
        await DriftAsync(h);

        var actions = Assert.Single(h.Receiver.Requests).Headers["Actions"];
        Assert.StartsWith("http, Acknowledge, https://talkwatch.test/a/", actions, StringComparison.Ordinal);
        var token = AckLink().Match(actions).Groups[1].Value;
        using var stranger = TalkWatchApp.Browser(h.App);

        // Opening the page, as a mail scanner would, changes nothing; a forged token finds nothing.
        var page = await stranger.GetStringAsync(new Uri($"/a/{token}", UriKind.Relative), Ct);
        var forged = await stranger.PostAsync(new Uri($"/a/{token[..^4]}AAAA/ack", UriKind.Relative), null, Ct);
        Assert.Contains("TalkWatch stopped copying calls", page, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/events/{Guid.NewGuid()}/ack")).StatusCode);
        Assert.Null(await h.DbAsync(db => db.AlertEvents.Select(e => e.AcknowledgedAt).SingleAsync(Ct)));

        var ack = await stranger.PostAsync(new Uri($"/a/{token}/ack", UriKind.Relative), null, Ct);

        Assert.Equal(HttpStatusCode.Redirect, ack.StatusCode);
        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(Ct));
        Assert.Equal((h.Clock.Now, "link"), (alert.AcknowledgedAt, alert.AcknowledgedBy));
        Assert.Contains("data-acknowledged", await stranger.GetStringAsync(new Uri($"/a/{token}", UriKind.Relative), Ct), StringComparison.Ordinal);
        Assert.Equal(1, await h.DbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "alert.ack", Ct)));

        // Whoever pressed it, and everyone else the alert reached, hear that it is in hand: quietly, with nothing to press.
        await h.DispatchAsync();
        var confirmation = h.Receiver.Requests[1];
        Assert.Equal(2, h.Receiver.Requests.Count);
        Assert.Equal("Acknowledged: TalkWatch stopped copying calls", confirmation.Headers["Title"]);
        Assert.Equal(("low", "white_check_mark"), (confirmation.Headers["Priority"], confirmation.Headers["Tags"]));
        Assert.False(confirmation.Headers.ContainsKey("Actions"));
        Assert.Contains("Acknowledged from a notification at", Encoding.UTF8.GetString(confirmation.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unacknowledged_alert_escalates_once_after_its_time()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var first = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        var manager = await h.AddChannelAsync("Manager", ChannelKind.Webhook, "https://hooks.test/manager");
        await h.AddFlowAsync("Drift", new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(first)]), new WaitStep(10), new NotifyStep([FlowRecipient.ToChannel(manager)], Urgent: true)],
        });
        await DriftAsync(h);

        h.Clock.Now += TimeSpan.FromMinutes(9);
        await h.DispatchAsync();
        Assert.Single(h.Receiver.Requests);

        h.Clock.Now += TimeSpan.FromMinutes(2);
        await h.DispatchAsync();
        await h.DispatchAsync();

        Assert.Equal(["https://hooks.test/desk", "https://hooks.test/manager"], h.Receiver.Requests.Select(r => r.Uri.ToString()));
        using var body = JsonDocument.Parse(h.Receiver.Requests[1].Body);
        Assert.True(body.RootElement.GetProperty("escalation").GetBoolean());
        Assert.True(body.RootElement.GetProperty("urgent").GetBoolean());
        Assert.Equal(1, body.RootElement.GetProperty("stage").GetInt32());
        Assert.StartsWith("Still not picked up", body.RootElement.GetProperty("title").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Acknowledging_stops_escalation_and_snoozing_delays_it()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var first = await h.AddChannelAsync("Desk", ChannelKind.Webhook, "https://hooks.test/desk");
        var manager = await h.AddChannelAsync("Manager", ChannelKind.Webhook, "https://hooks.test/manager");
        await h.AddFlowAsync("Drift", new FlowDefinition
        {
            Trigger = AlertEventType.Drift,
            Steps = [new NotifyStep([FlowRecipient.ToChannel(first)]), new WaitStep(10), new NotifyStep([FlowRecipient.ToChannel(manager)], Urgent: true)],
        });
        await DriftAsync(h);
        var links = h.App.Services.GetRequiredService<AlertLinks>();
        var alert = await h.DbAsync(db => db.AlertEvents.SingleAsync(Ct));
        using var stranger = TalkWatchApp.Browser(h.App);

        await stranger.PostAsync(new Uri($"/a/{links.Token(alert.Id)}/snooze?minutes=30", UriKind.Relative), null, Ct);
        h.Clock.Now += TimeSpan.FromMinutes(20);
        await h.DispatchAsync();
        Assert.Single(h.Receiver.Requests);

        await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/events/{alert.Id}/ack");
        h.Clock.Now += TimeSpan.FromHours(2);
        await h.DispatchAsync();

        // The desk, which the alert reached, hears who acknowledged it; the escalation channel never hears of it at all.
        Assert.Equal(["https://hooks.test/desk", "https://hooks.test/desk"], h.Receiver.Requests.Select(r => r.Uri.ToString()));
        Assert.Equal("Acknowledged", h.Receiver.Requests[1].Headers["X-TalkWatch-Event"]);
        using var body = JsonDocument.Parse(h.Receiver.Requests[1].Body);
        Assert.Equal($"Acknowledged: TalkWatch stopped copying calls", body.RootElement.GetProperty("title").GetString());
        Assert.Equal(TalkWatchApp.AdminUsername, body.RootElement.GetProperty("acknowledged").GetProperty("by").GetString());
        Assert.Contains($"Acknowledged by {TalkWatchApp.AdminUsername} at", body.RootElement.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(TalkWatchApp.AdminUsername, await h.DbAsync(db => db.AlertEvents.Select(e => e.AcknowledgedBy).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Alerts_in_quiet_hours_wait_for_them_to_end_and_are_dropped_if_acknowledged_meanwhile()
    {
        // Wednesday 23:00 in London; quiet every night 22:00 to 07:00.
        await using var h = await StartAsync(DateTimeOffset.Parse("2026-09-30T22:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var channel = await h.AddChannelAsync("Phone", ChannelKind.Webhook, "https://hooks.test/phone", more:
            [.. Enum.GetValues<DayOfWeek>().Select(d => ("QuietDays", d.ToString())), ("QuietStart", "22:00"), ("QuietEnd", "07:00")]);
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);
        await DriftAsync(h);

        var held = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(Ct));
        Assert.Empty(h.Receiver.Requests);
        Assert.Equal((DeliveryState.Pending, 0), (held.State, held.Attempts));
        Assert.Equal(DateTimeOffset.Parse("2026-10-01T06:00:00Z", System.Globalization.CultureInfo.InvariantCulture), held.NextAttemptAt);

        h.Clock.Now = held.NextAttemptAt;
        await h.DispatchAsync();
        Assert.Single(h.Receiver.Requests);

        // The next night: acknowledged before morning, so never sent.
        h.Console.Drifted = false;
        await h.PollAsync();
        h.Clock.Now += TimeSpan.FromHours(16);
        await DriftAsync(h);
        var second = await h.DbAsync(db => db.AlertEvents.OrderByDescending(e => e.At).Select(e => e.Id).FirstAsync(Ct));
        await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/events/{second}/ack");
        h.Clock.Now += TimeSpan.FromHours(10);
        await h.DispatchAsync();

        Assert.Single(h.Receiver.Requests);
        Assert.Equal(DeliveryState.Cancelled, await h.DbAsync(db => db.AlertDeliveries.Where(d => d.EventId == second).Select(d => d.State).SingleAsync(Ct)));
    }

    [Fact]
    public async Task Word_of_an_acknowledgement_due_in_quiet_hours_is_dropped_not_held_until_morning()
    {
        // Wednesday 21:00 in London, an hour before the channel's quiet hours start.
        await using var h = await StartAsync(DateTimeOffset.Parse("2026-09-30T20:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var channel = await h.AddChannelAsync("Phone", ChannelKind.Webhook, "https://hooks.test/phone", more:
            [.. Enum.GetValues<DayOfWeek>().Select(d => ("QuietDays", d.ToString())), ("QuietStart", "22:00"), ("QuietEnd", "07:00")]);
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);
        await DriftAsync(h);
        var alert = await h.DbAsync(db => db.AlertEvents.Select(e => e.Id).SingleAsync(Ct));

        h.Clock.Now += TimeSpan.FromHours(2);
        await PostAsync(h.Admin, "/admin/alerts", $"/admin/alerts/events/{alert}/ack");
        await h.DispatchAsync();
        h.Clock.Now += TimeSpan.FromHours(12);
        await h.DispatchAsync();

        Assert.Single(h.Receiver.Requests);
        var word = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(d => d.Acknowledgement, Ct));
        Assert.Equal(DeliveryState.Cancelled, word.State);
    }

    [Fact]
    public async Task An_email_channel_sends_through_the_configured_mail_server()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await StartAsync(LongAfterTheFixtures, s => s.Configure<SmtpOptions>(o =>
        {
            (o.Host, o.Port, o.From, o.StartTls) = ("127.0.0.1", smtp.Port, "talkwatch@example.test", false);
        }));
        var channel = await h.AddChannelAsync("Office mail", ChannelKind.Email, "office@example.test");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);

        await DriftAsync(h);

        var mail = Assert.Single(smtp.Messages);
        Assert.Equal(["office@example.test"], mail.To);
        Assert.Contains("Subject: TalkWatch stopped copying calls", mail.Data, StringComparison.Ordinal);
        Assert.Contains("https://talkwatch.test/a/", mail.Data, StringComparison.Ordinal);
        Assert.Equal(DeliveryState.Sent, await h.DbAsync(db => db.AlertDeliveries.Select(d => d.State).SingleAsync(Ct)));
    }

    // Shaped like a bot token so validation accepts it, but not like a real one, so secret scanners leave it alone.
    private const string BotToken = "123456:test-token-not-real-0001";
    private const string SiteBotToken = "654321:site-token-not-real-0002";

    [Fact]
    public async Task A_telegram_channel_sends_through_its_own_bot_with_an_acknowledge_button()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Group", ChannelKind.Telegram, "-1001234567890", BotToken);
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);

        await DriftAsync(h);

        var sent = Assert.Single(h.Receiver.Requests);
        Assert.Equal($"https://api.telegram.org/bot{BotToken}/sendMessage", sent.Uri.ToString());
        using var body = JsonDocument.Parse(sent.Body);
        Assert.Equal("-1001234567890", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("HTML", body.RootElement.GetProperty("parse_mode").GetString());
        Assert.StartsWith("<b>TalkWatch stopped copying calls</b>\n", body.RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
        var button = body.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0];
        Assert.StartsWith("https://talkwatch.test/a/", button.GetProperty("url").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(BotToken, await h.DbAsync(db => db.AlertChannels.Select(c => c.ProtectedSecret).SingleAsync(Ct)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_telegram_channel_left_empty_uses_the_site_settings()
    {
        await using var h = await StartAsync(LongAfterTheFixtures, s => s.Configure<TelegramOptions>(o => (o.BotToken, o.ChatId) = (SiteBotToken, "42")));
        var fromSettings = await h.AddChannelAsync("Site", ChannelKind.Telegram, "");
        var ownChat = await h.AddChannelAsync("Other chat", ChannelKind.Telegram, "@talkwatch_alerts");
        await h.SendToAsync("Drift", AlertEventType.Drift, [fromSettings, ownChat]);

        await DriftAsync(h);

        Assert.All(h.Receiver.Requests, r => Assert.Equal($"https://api.telegram.org/bot{SiteBotToken}/sendMessage", r.Uri.ToString()));
        var chats = h.Receiver.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("chat_id").GetString()).Order(StringComparer.Ordinal);
        Assert.Equal(["42", "@talkwatch_alerts"], chats);
    }

    [Theory]
    [InlineData("", BotToken)]           // no chat, and none in the settings
    [InlineData("-100123", "")]          // no token, and none in the settings
    [InlineData("hello", BotToken)]      // not a chat id
    [InlineData("-100123", "not-a-token")]
    public async Task A_telegram_channel_needs_a_chat_and_a_token_from_somewhere(string chat, string token)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);

        await PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/channels", ("Name", "Group"), ("Kind", "Telegram"), ("Target", chat), ("Secret", token), ("Owner", ""));

        Assert.Equal(0, await h.DbAsync(db => db.AlertChannels.CountAsync(Ct)));
    }

    [Fact]
    public async Task A_telegram_failure_is_kept_without_the_token()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        var channel = await h.AddChannelAsync("Group", ChannelKind.Telegram, "-100123", BotToken);
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);
        h.Receiver.Status = HttpStatusCode.BadRequest;

        await DriftAsync(h);

        var failed = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(Ct));
        Assert.Contains("HTTP 400", failed.LastError, StringComparison.Ordinal);
        Assert.DoesNotContain(BotToken, failed.LastError!, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> SaveSettingsAsync(Harness h, params (string Name, string Value)[] fields) =>
        PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/settings", fields);

    [Fact]
    public async Task The_mail_server_can_be_set_on_the_alerts_page_alone()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await StartAsync(LongAfterTheFixtures);
        await SaveSettingsAsync(h, ("SmtpHost", "127.0.0.1"), ("SmtpPort", smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("SmtpFrom", "talkwatch@example.test"), ("SmtpStartTls", "no"));
        var channel = await h.AddChannelAsync("Office mail", ChannelKind.Email, "office@example.test");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);

        await DriftAsync(h);

        Assert.Equal(["office@example.test"], Assert.Single(smtp.Messages).To);
        Assert.Equal(1, await h.DbAsync(db => db.AuditEvents.CountAsync(e => e.Action == "alert.settings", Ct)));
    }

    [Fact]
    public async Task Settings_saved_on_the_page_win_field_by_field_and_secrets_are_never_shown()
    {
        await using var h = await StartAsync(LongAfterTheFixtures, s => s.Configure<TelegramOptions>(o => (o.BotToken, o.ChatId) = (SiteBotToken, "42")));
        await SaveSettingsAsync(h, ("TelegramBotToken", BotToken), ("TelegramChatId", ""));
        var channel = await h.AddChannelAsync("Group", ChannelKind.Telegram, "");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);

        await DriftAsync(h);

        // The saved token wins; the chat, left empty, comes from the settings.
        var sent = Assert.Single(h.Receiver.Requests);
        Assert.Equal($"https://api.telegram.org/bot{BotToken}/sendMessage", sent.Uri.ToString());
        Assert.Equal("42", JsonDocument.Parse(sent.Body).RootElement.GetProperty("chat_id").GetString());
        var page = await h.Admin.GetStringAsync(new Uri("/admin/alerts", UriKind.Relative), Ct);
        Assert.DoesNotContain(BotToken, page, StringComparison.Ordinal);
        Assert.DoesNotContain(SiteBotToken, page, StringComparison.Ordinal);
        Assert.Contains("Saved here", page, StringComparison.Ordinal);

        // Saving again with the token left empty keeps it; forgetting it falls back to the settings.
        await SaveSettingsAsync(h, ("TelegramChatId", "-100777"));
        Assert.Equal(BotToken, (await h.App.Services.GetRequiredService<AlertSettingsStore>().TelegramAsync(Ct)).BotToken);
        await SaveSettingsAsync(h, ("ClearTelegramBotToken", "true"), ("TelegramChatId", "-100777"));
        var now = await h.App.Services.GetRequiredService<AlertSettingsStore>().TelegramAsync(Ct);
        Assert.Equal((SiteBotToken, "-100777"), (now.BotToken, now.ChatId));
    }

    // A port for reading mail (IMAP's 993) took the mail server's TLS handshake for silence: each try waited until it
    // timed out, and said only that. Now it fails at once, saying what the port is for.
    [Fact]
    public async Task A_port_for_reading_mail_fails_at_once_and_says_so()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        await h.DbAsync(async db =>
        {
            db.AlertSettings.Add(new AlertSettings { SiteId = await db.Sites.Select(s => s.Id).SingleAsync(Ct), SmtpHost = "127.0.0.1", SmtpPort = 993, SmtpFrom = "talkwatch@example.test" });
            return await db.SaveChangesAsync(Ct);
        });
        var channel = await h.AddChannelAsync("Office mail", ChannelKind.Email, "office@example.test");
        await h.SendToAsync("Drift", AlertEventType.Drift, [channel]);

        var started = DateTimeOffset.UtcNow;
        await DriftAsync(h);

        var failed = await h.DbAsync(db => db.AlertDeliveries.SingleAsync(Ct));
        Assert.Contains("993 is for reading mail", failed.LastError, StringComparison.Ordinal);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(20));
    }

    // Strict mail servers refuse a HELO name that is not a domain, and a container's own name is a bare id: TalkWatch
    // greets with its public host, or the From address's domain.
    [Fact]
    public async Task A_test_email_goes_now_greeting_the_server_by_a_real_name_and_says_what_happened()
    {
        await using var smtp = new FakeSmtpServer();
        await using var h = await StartAsync(LongAfterTheFixtures);
        await SaveSettingsAsync(h, ("SmtpHost", "127.0.0.1"), ("SmtpPort", smtp.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("SmtpFrom", "talkwatch@example.test"), ("SmtpStartTls", "no"));

        var sent = await PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/settings/test", ("To", "office@example.test"));

        var message = Assert.Single(smtp.Messages);
        Assert.Equal(["office@example.test"], message.To);
        Assert.Contains("Subject: TalkWatch test email", message.Data, StringComparison.Ordinal);
        var expected = h.App.Services.GetRequiredService<IOptions<SiteOptions>>().Value.PublicUrl?.Host ?? "example.test";
        Assert.Equal($"EHLO {expected}", Assert.Single(smtp.Greetings));
        Assert.Contains("Test email sent to office@example.test", Uri.UnescapeDataString(sent.Headers.Location!.OriginalString), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_email_that_cannot_go_says_why_on_the_page()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);
        // Nothing listens here.
        var free = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        free.Start();
        var port = ((System.Net.IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        await SaveSettingsAsync(h, ("SmtpHost", "127.0.0.1"), ("SmtpPort", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("SmtpFrom", "talkwatch@example.test"), ("SmtpStartTls", "no"));

        var sent = await PostAsync(h.Admin, "/admin/alerts", "/admin/alerts/settings/test", ("To", "office@example.test"));

        var said = Uri.UnescapeDataString(sent.Headers.Location!.OriginalString);
        Assert.Contains("The test email to office@example.test was not sent.", said, StringComparison.Ordinal);
        Assert.Contains($"Could not reach the mail server 127.0.0.1 on port {port}", said, StringComparison.Ordinal);
    }

    // A port typed in is kept and shown again; one refused says why beside the settings, where it was typed.
    [Fact]
    public async Task A_saved_port_is_shown_again_and_a_refused_one_says_why_beside_the_settings()
    {
        await using var h = await StartAsync(LongAfterTheFixtures);

        await SaveSettingsAsync(h, ("SmtpHost", "mail.example.test"), ("SmtpPort", "2525"), ("SmtpFrom", "talkwatch@example.test"));
        Assert.Equal(2525, await h.DbAsync(db => db.AlertSettings.Select(s => s.SmtpPort).SingleAsync(Ct)));
        Assert.Contains("name=\"SmtpPort\" type=\"number\" min=\"1\" max=\"65535\" value=\"2525\"", await h.Admin.GetStringAsync(new Uri("/admin/alerts", UriKind.Relative), Ct), StringComparison.Ordinal);

        var refused = await SaveSettingsAsync(h, ("SmtpHost", "mail.example.test"), ("SmtpPort", "993"), ("SmtpFrom", "talkwatch@example.test"));
        var back = refused.Headers.Location!.OriginalString;
        Assert.EndsWith("#settings", back, StringComparison.Ordinal);
        var page = WebUtility.HtmlDecode(await h.Admin.GetStringAsync(new Uri(back.Split('#')[0], UriKind.Relative), Ct));
        var settings = page[page.IndexOf("aria-label=\"Settings\"", StringComparison.Ordinal)..];
        Assert.Contains("Port 993 is for reading mail", settings, StringComparison.Ordinal);
        Assert.Equal(2525, await h.DbAsync(db => db.AlertSettings.Select(s => s.SmtpPort).SingleAsync(Ct)));
    }

    [Theory]
    [InlineData("SmtpPort", "70000")]
    [InlineData("SmtpPort", "993")]
    [InlineData("SmtpFrom", "not an address")]
    [InlineData("TelegramChatId", "hello")]
    [InlineData("TelegramBotToken", "not-a-token")]
    public async Task Settings_that_cannot_work_are_refused(string field, string value)
    {
        await using var h = await StartAsync(LongAfterTheFixtures);

        await SaveSettingsAsync(h, (field, value));

        Assert.Equal(0, await h.DbAsync(db => db.AlertSettings.CountAsync(Ct)));
    }
}

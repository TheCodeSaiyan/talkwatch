using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Alerts;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Replay;

namespace TalkWatch.Web.Services;

/// <summary>
/// What makes a demo look alive: on a new demo database, example flows and alerts in TalkWatch for the first admin; and
/// every few minutes a new call on the replayed console, stamped now, so Now, Operator, the flows and the bell have
/// something happening. Only in demo mode, and only on the replay: nothing here can reach a real console.
/// </summary>
public sealed partial class DemoActivity(
    FixtureConsole console, IServiceScopeFactory scopes, LineDirectorySync directory, IOptions<DemoOptions> options, IOptions<BootstrapOptions> bootstrap,
    TimeProvider clock, ILogger<DemoActivity> logger) : BackgroundService
{
    // Fictional numbers from Ofcom's drama range, and the captured contacts' mobile, so some callers are known.
    private static readonly string[] Callers = ["+447700900111", "+447700900232", "+447700900880", "+447700900417", "+447700900568", "+447700900693"];

    private int _turn;
    private DateTimeOffset _nextCall = DateTimeOffset.MaxValue;

    /// <summary>When the demo next starts over; null when it never does. The banner counts down to it.</summary>
    public DateTimeOffset? NextResetAt { get; private set; }
    private readonly List<(string Uuid, string From, DateTimeOffset Started, bool Answered, bool Outside)> _inProgress = [];

    // One guest set-up at a time: the demo starting, a reset and a test can all ask at once, and two password resets on
    // the one account at once fail on its concurrency stamp.
    private readonly SemaphoreSlim _guest = new(1, 1);

    /// <summary>How long a call in progress rings before it is answered, and how long it lasts in all.</summary>
    public static readonly TimeSpan RingsFor = TimeSpan.FromSeconds(40), LastsFor = TimeSpan.FromSeconds(150);

    /// <summary>
    /// Once a tick: when the interval is up, a finished call of the next kind and a new call in progress; and each call
    /// in progress moved on, ringing to answered to ended, so Now and Operator have something going on.
    /// </summary>
    public void Tick(DateTimeOffset now)
    {
        if (now >= _nextCall)
        {
            AddCall();
            StartCall(now);
            _nextCall = now + TimeSpan.FromMinutes(Math.Max(1, options.Value.CallEveryMinutes));
        }

        for (var i = _inProgress.Count - 1; i >= 0; i--)
        {
            var call = _inProgress[i];
            if (now >= call.Started + LastsFor)
            {
                console.UpsertCall(InProgress(call.Uuid, call.From, call.Started, answered: true, ended: true, call.Outside));
                _inProgress.RemoveAt(i);
            }
            else if (!call.Answered && now >= call.Started + RingsFor)
            {
                console.UpsertCall(InProgress(call.Uuid, call.From, call.Started, answered: true, ended: false, call.Outside));
                _inProgress[i] = call with { Answered = true };
            }
        }
    }

    /// <summary>
    /// A call that has just started ringing the ring group: on Now as ringing until it is answered. Every third is put
    /// through to an outside phone instead, which the demo's people carry, so its alert can be seen arriving.
    /// </summary>
    public void StartCall(DateTimeOffset now)
    {
        var uuid = Guid.NewGuid().ToString();
        var from = Callers[(_turn + 3) % Callers.Length];
        var outside = ++_started % 3 == 0;
        console.UpsertCall(InProgress(uuid, from, now, answered: false, ended: false, outside));
        _inProgress.Add((uuid, from, now, false, outside));
    }

    private int _started;

    // The same call as Talk reports it at each point: ringing, answered by someone in the group or on the outside phone,
    // then ended.
    private string InProgress(string uuid, string from, DateTimeOffset started, bool answered, bool ended, bool outside = false)
    {
        var group = directory.Current.Groups.FirstOrDefault(g => g.MemberList is { Count: > 0 });
        var member = outside ? null : group?.MemberList?[0];
        string Event(DateTimeOffset at, string name, string data = "{}") => $$"""{"time":"{{At(at)}}","event":"{{name}}","event_data":{{data}},"event_uuid":"{{uuid}}-{{name}}"}""";
        var events = new List<string>
        {
            Event(started, "call_started"),
            Event(started.AddSeconds(2), "seq_call_trying_endpoints", outside ? $$"""{"contact_uuids":["{{ContactWithEmail}}"]}""" : "{}"),
        };
        if (answered)
        {
            events.Add(Event(started + RingsFor, "call_accepted", outside ? $$"""{"accepted_by_contact_uuid":"{{ContactWithEmail}}"}""" : AcceptedBy(member)));
        }

        if (ended)
        {
            events.Add(Event(started + LastsFor, "call_hangup"));
        }

        var extra = (group is null ? "" : $$""","to_group_id":"{{group.Id}}" """) + (answered && member is not null ? $$""","answered_by_user_uuid":"{{member}}" """ : "");
        return $$"""
            {"uuid":"{{uuid}}","time":"{{At(started)}}","direction":"in","status":"{{(answered ? "accepted" : "ringing")}}","duration":{{(ended ? (int)LastsFor.TotalSeconds : 0)}},"from":"{{from}}","to":"+441144960042","country":"GB"{{extra}},
             "call_events":[{{string.Join(",", events)}}]}
            """;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await StepAsync(GuestAsync, stoppingToken);
            await StepAsync(SeedAsync, stoppingToken);
            if (options.Value.CallEveryMinutes <= 0 && options.Value.ResetMinutes <= 0)
            {
                return;
            }

            // The first new calls one interval after starting: the replay's own calls are all there is until then, as
            // they always were. A tick every 15 seconds moves calls in progress on.
            _nextCall = clock.GetUtcNow() + TimeSpan.FromMinutes(options.Value.CallEveryMinutes);
            NextResetAt = options.Value.ResetMinutes > 0 ? clock.GetUtcNow() + TimeSpan.FromMinutes(options.Value.ResetMinutes) : null;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                if (clock.GetUtcNow() >= NextResetAt)
                {
                    await StepAsync(ResetAsync, stoppingToken);
                }

                Tick(clock.GetUtcNow());
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // A step that fails is logged and the demo goes on: a background service that throws stops the whole app.
    private async Task StepAsync(Func<CancellationToken, Task> step, CancellationToken stoppingToken)
    {
        try
        {
            await step(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // The demo carries on; the next start-over tries again.
        catch (Exception e)
#pragma warning restore CA1031
        {
            LogStepFailed(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo: a step failed; carrying on.")]
    private static partial void LogStepFailed(ILogger logger, Exception exception);

    // What a reset keeps: who can sign in (people, roles, their sessions' keys), the site, and the console's directory of
    // lines and switchboards, which the replay would only write back the same.
    private static readonly string[] Kept = ["sites", "lines", "switchboard_nodes"];

    /// <summary>
    /// Starts the demo over: the replay back to its captured calls, every table of what the demo has gathered emptied,
    /// and the guest and the example flows set up again. Only ever in demo mode, which is the only place this runs.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        console.StartOver(now.AddHours(-1));
        (_turn, _nextCall) = (0, now + TimeSpan.FromMinutes(Math.Max(1, options.Value.CallEveryMinutes)));
        _inProgress.Clear();

        using (var scope = scopes.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName()).OfType<string>().Distinct()
                .Where(t => !t.StartsWith("AspNet", StringComparison.Ordinal) && !t.Contains("DataProtection", StringComparison.OrdinalIgnoreCase) && !Kept.Contains(t))
                .ToList();
            var sql = "TRUNCATE TABLE " + string.Join(", ", tables.Select(t => "\"" + t.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")) + " CASCADE";
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken);
        }

        await GuestAsync(cancellationToken);
        await SeedAsync(cancellationToken);
        NextResetAt = options.Value.ResetMinutes > 0 ? now + TimeSpan.FromMinutes(options.Value.ResetMinutes) : null;
        LogReset(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo: started over.")]
    private static partial void LogReset(ILogger logger);

    /// <summary>The role the demo guest holds: sees everything, changes nothing that matters.</summary>
    public const string GuestRole = "Demo guest";

    // An admin, so visitors can try everything; what would change the site for everyone after them, or send anything
    // out of the browser, is refused by DemoGuard instead. Not API tokens: those are for reaching TalkWatch from outside.
    private const Permission GuestPermissions = Permission.All & ~Permission.ApiTokens;

    /// <summary>
    /// Every start: the shared guest, its role and its alerts in TalkWatch, with the published password set again so
    /// nobody can lock others out by changing it. Never locked out by failed sign-ins either.
    /// </summary>
    public async Task GuestAsync(CancellationToken cancellationToken)
    {
        await _guest.WaitAsync(cancellationToken);
        try
        {
            await SetUpGuestAsync(cancellationToken);
        }
        finally
        {
            _guest.Release();
        }
    }

    private async Task SetUpGuestAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.GuestUsername.Length == 0 || o.GuestPassword.Length == 0)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        if (await roles.FindByNameAsync(GuestRole) is not { } role)
        {
            role = new IdentityRole<Guid>(GuestRole);
            await roles.CreateAsync(role);
        }

        if (await RolePermissions.GetAsync(roles, role) != GuestPermissions)
        {
            await RolePermissions.ChangeAsync(roles, role, GuestPermissions);
        }

        if (await users.FindByNameAsync(o.GuestUsername) is not { } guest)
        {
            guest = new AppUser { UserName = o.GuestUsername, SiteId = site.Id, LockoutEnabled = false };
            var created = await users.CreateAsync(guest, o.GuestPassword);
            if (!created.Succeeded)
            {
                LogGuestFailed(logger, string.Join("; ", created.Errors.Select(e => e.Description)));
                return;
            }
        }
        else
        {
            await users.RemovePasswordAsync(guest);
            await users.AddPasswordAsync(guest, o.GuestPassword);
            // Everyone shares the guest, so a number one visitor chose goes back to all numbers at each start-over.
            if (guest.TwoFactorEnabled || guest.LockoutEnabled || guest.ContextDid is not null)
            {
                (guest.TwoFactorEnabled, guest.LockoutEnabled, guest.LockoutEnd, guest.ContextDid) = (false, false, null, null);
                await users.UpdateAsync(guest);
            }
        }

        if (!await users.IsInRoleAsync(guest, GuestRole))
        {
            await users.RemoveFromRolesAsync(guest, await users.GetRolesAsync(guest));
            await users.AddToRoleAsync(guest, GuestRole);
        }

        if (!await db.AlertChannels.AnyAsync(c => c.Kind == ChannelKind.Browser && c.OwnerUserId == guest.Id, cancellationToken))
        {
            db.AlertChannels.Add(new AlertChannel
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = $"{guest.UserName}'s browser", Kind = ChannelKind.Browser, Target = "", OwnerUserId = guest.Id,
                CreatedAt = clock.GetUtcNow(),
            });
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>How many example flows a new demo gets.</summary>
    public const int ExampleFlows = 10;

    // Contacts in the replayed console: one Talk holds an email for, so a flow can tell them, and one it holds none for,
    // which the flow editor shows would hear nothing.
    internal const string ContactWithEmail = "ac7a4901-2722-4e41-9f20-87f95df72cb2", ContactWithoutEmail = "7624c978-2957-44f4-a42a-93ccd47a0bb8";

    /// <summary>
    /// On a demo database: alerts in TalkWatch for the first admin, the admin linked to someone in a ring group so
    /// 'whoever it rang' reaches them, and flows that use what TalkWatch can do; people holding roles on the number;
    /// and reports, each run once so there is a copy to open. Each part only when it has none yet, so changes made in
    /// the demo stay, and a demo from before a part existed gets that part at its next start.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Seed || bootstrap.Value.AdminUsername is not { Length: > 0 } adminName)
        {
            return;
        }

        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var site = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.FindByNameAsync(adminName) is not { } admin)
        {
            return;
        }

        // The first poll loads the directory; refreshing it here as well raced that and saved the same lines twice.
        for (var waited = TimeSpan.Zero; Member() is null && waited < TimeSpan.FromMinutes(2); waited += TimeSpan.FromSeconds(2))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), clock, cancellationToken);
        }
        if (Member() is { } member && admin.TalkUserUuid is null)
        {
            admin.TalkUserUuid = member;
            await users.UpdateAsync(admin);
        }

        var now = clock.GetUtcNow();
        var numberAdmin = await SeedNumberPeopleAsync(scope.ServiceProvider, db, site, now, cancellationToken);
        await SeedReportsAsync(scope.ServiceProvider, db, site, numberAdmin, now, cancellationToken);

        // The outside phone the demo's calls are put through to, carried by the admin and the guest.
        if (!await db.ContactLinks.AnyAsync(cancellationToken))
        {
            var guest = options.Value.GuestUsername.Length > 0 && await users.FindByNameAsync(options.Value.GuestUsername) is { } carrier ? carrier.Id : (Guid?)null;
            foreach (var person in guest is { } id ? [admin.Id, id] : new[] { admin.Id })
            {
                db.ContactLinks.Add(new ContactLink { SiteId = site.Id, ContactUuid = ContactWithEmail, UserId = person, CreatedAt = now });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        if (await db.AlertFlows.AnyAsync(cancellationToken))
        {
            return;
        }

        if (!await db.AlertChannels.AnyAsync(c => c.Kind == ChannelKind.Browser && c.OwnerUserId == admin.Id, cancellationToken))
        {
            db.AlertChannels.Add(new AlertChannel
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = $"{admin.UserName}'s browser", Kind = ChannelKind.Browser, Target = "", OwnerUserId = admin.Id, CreatedAt = now,
            });
        }

        // Everyone the demo has: the admin, and the guest when there is one, so whoever is looking sees alerts arrive.
        var guestId = options.Value.GuestUsername.Length > 0 && await users.FindByNameAsync(options.Value.GuestUsername) is { } g ? g.Id : (Guid?)null;
        FlowRecipient[] everyone = guestId is { } gid ? [FlowRecipient.ToPerson(admin.Id), FlowRecipient.ToPerson(gid)] : [FlowRecipient.ToPerson(admin.Id)];
        (string Name, FlowDefinition Flow)[] flows =
        [
            ("Missed calls", new FlowDefinition
            {
                Trigger = AlertEventType.MissedCall,
                Steps = [new NotifyStep([FlowRecipient.ToRang(), .. everyone], Include: NotifyIncludes.Summary), new WaitStep(5), new NotifyStep(everyone, Urgent: true)],
            }),
            ("Callers who try again", new FlowDefinition
            {
                Trigger = AlertEventType.MissedCall,
                Conditions = [new RepeatCallerCondition(2, 60)],
                Steps = [new NotifyStep(everyone, Urgent: true), new AssignStep(admin.Id)],
            }),
            ("Voicemail, gathered", new FlowDefinition
            {
                Trigger = AlertEventType.Voicemail,
                Steps = [new BundleStep(15), new NotifyStep(everyone)],
            }),
            ("Hung up in the menu", new FlowDefinition
            {
                Trigger = AlertEventType.HungUpAtSwitchboard,
                Steps = [new BranchStep(new KnownCallerCondition(true), Then: [new NotifyStep(everyone, Urgent: true)], Otherwise: [new NotifyStep(everyone)])],
            }),
            ("Poor call quality", new FlowDefinition { Trigger = AlertEventType.PoorQualityCall, Steps = [new NotifyStep(everyone)] }),
            ("Callers we keep missing", new FlowDefinition
            {
                Trigger = AlertEventType.MissedCall,
                Conditions = [new CallerMissedCondition(2, 120)],
                Steps =
                [
                    new NotifyStep([.. everyone, FlowRecipient.ToContact(ContactWithEmail), FlowRecipient.ToContact(ContactWithoutEmail)], Urgent: true,
                        Include: NotifyIncludes.Summary | NotifyIncludes.Voicemail),
                    new AssignStep(admin.Id),
                ],
            }),
            ("Voicemail that sounds urgent", new FlowDefinition
            {
                Trigger = AlertEventType.VoicemailTranscribed,
                Steps =
                [
                    new BranchStep(new TranscriptWordsCondition(["urgent", "emergency", "today"]),
                        Then: [new NotifyStep(everyone, Urgent: true, Include: NotifyIncludes.Transcript | NotifyIncludes.Voicemail)],
                        Otherwise: [new NotifyStep(everyone, Include: NotifyIncludes.Summary)]),
                ],
            }),
            ("Answer rate slipping", new FlowDefinition
            {
                Trigger = AlertEventType.MissedCall,
                Conditions = [new AnswerRateCondition(70, 2)],
                Steps = [new NotifyStep(everyone)],
            }),
            ("Put through to a mobile", new FlowDefinition
            {
                Trigger = AlertEventType.InboundCall,
                Conditions = [new ForwardedOutsideCondition(true)],
                Steps = [new NotifyStep([FlowRecipient.ToOutside(OutsideScope.ThisCall)])],
            }),
            ("Call-backs piling up", new FlowDefinition
            {
                Trigger = AlertEventType.MissedCall,
                Steps = [new BranchStep(new WaitingCallersCondition(3), Then: [new NotifyStep(everyone, Urgent: true)], Otherwise: [new NotifyStep([FlowRecipient.ToRang()])])],
            }),
        ];
        foreach (var (name, flow) in flows)
        {
            db.AlertFlows.Add(new AlertFlow
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = name, Trigger = flow.Trigger, Definition = Flows.Write(flow), CreatedAt = now, UpdatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        LogSeeded(logger, flows.Length, adminName);
    }

    // The demo's own people: nobody signs in as them (they have no password); they are there to be seen holding roles.
    private static readonly (string Name, string Email)[] People =
        [("alex.morgan", "alex.morgan@example.invalid"), ("sam.patel", "sam.patel@example.invalid"), ("jo.fenwick", "jo.fenwick@example.invalid")];

    private const string NumberAdminRole = "Number admin", NumberViewerRole = "Number viewer";

    /// <summary>
    /// People holding roles on the account's number, when nobody holds one yet: one who looks after it and may give its
    /// roles, and two who see it. Returns the one who looks after it, for the number's report; null without a number.
    /// </summary>
    private static async Task<AppUser?> SeedNumberPeopleAsync(IServiceProvider services, TalkWatchDbContext db, CurrentSite site, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var did = await Did(db, cancellationToken);
        if (did is null)
        {
            return null;
        }

        var users = services.GetRequiredService<UserManager<AppUser>>();
        var roles = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var people = new List<AppUser>();
        foreach (var (name, email) in People)
        {
            if (await users.FindByNameAsync(name) is not { } person)
            {
                person = new AppUser { UserName = name, Email = email, EmailConfirmed = true, SiteId = site.Id };
                if (!(await users.CreateAsync(person)).Succeeded)
                {
                    return null;
                }

                await users.AddToRoleAsync(person, Roles.Viewer);
            }

            people.Add(person);
        }

        async Task<IdentityRole<Guid>> RoleAsync(string name, Permission permissions)
        {
            if (await roles.FindByNameAsync(name) is not { } role)
            {
                role = new IdentityRole<Guid>(name);
                await roles.CreateAsync(role);
                await RolePermissions.ChangeAsync(roles, role, permissions);
            }

            return role;
        }

        var looksAfter = await RoleAsync(NumberAdminRole, Permissions.PerNumber);
        var sees = await RoleAsync(NumberViewerRole, Permission.AllAudio | Permission.AllTranscripts | Permission.MarkCallBacks | Permission.OwnAlerts);
        if (!await db.NumberRoles.AnyAsync(cancellationToken))
        {
            db.NumberRoles.AddRange(
                new NumberRole { Id = Guid.NewGuid(), SiteId = site.Id, UserId = people[0].Id, Did = did, RoleId = looksAfter.Id, CreatedAt = now },
                new NumberRole { Id = Guid.NewGuid(), SiteId = site.Id, UserId = people[1].Id, Did = did, RoleId = sees.Id, CreatedAt = now },
                new NumberRole { Id = Guid.NewGuid(), SiteId = site.Id, UserId = people[2].Id, Did = did, RoleId = sees.Id, CreatedAt = now });
            await db.SaveChangesAsync(cancellationToken);
        }

        return people[0];
    }

    /// <summary>
    /// Two reports, when there are none: the whole site every Monday, kept for those who manage reports, and the number
    /// every morning for the person who looks after it. Each run once now, over a period that takes in today, so the
    /// Reports page has a copy of each to open; nothing is emailed in a demo.
    /// </summary>
    private static async Task SeedReportsAsync(IServiceProvider services, TalkWatchDbContext db, CurrentSite site, AppUser? numberAdmin, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await db.Reports.AnyAsync(cancellationToken))
        {
            return;
        }

        var builder = services.GetRequiredService<ReportBuilder>();
        var reports = new List<Report>
        {
            new()
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = "Weekly service", Schedule = ReportSchedule.Weekly, Weekday = DayOfWeek.Monday, At = new TimeOnly(8, 0),
                Period = ReportPeriod.Week, Sections = ReportSections.Figures | ReportSections.CallBacks | ReportSections.Lines | ReportSections.Switchboard, CreatedAt = now,
            },
        };
        if (numberAdmin is not null && await Did(db, cancellationToken) is { } did)
        {
            var daily = new Report
            {
                Id = Guid.NewGuid(), SiteId = site.Id, Name = "Main line, daily", Schedule = ReportSchedule.Daily, At = new TimeOnly(7, 30), Period = ReportPeriod.Day,
                Sections = ReportSections.Figures | ReportSections.CallBacks, CreatedAt = now,
            };
            daily.Numbers.Add(new ReportNumber { ReportId = daily.Id, Did = did });
            daily.Recipients.Add(new ReportRecipient { Id = Guid.NewGuid(), ReportId = daily.Id, UserId = numberAdmin.Id });
            reports.Add(daily);
        }

        foreach (var report in reports)
        {
            report.NextRunAt = ReportTiming.Next(report, now, builder.Zone);
        }

        db.Reports.AddRange(reports);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var report in reports)
        {
            await builder.RunAsync(report.Id, now.AddDays(1), cancellationToken);
        }
    }

    // The account's first number, as E.164; null before the directory has loaded.
    private static Task<string?> Did(TalkWatchDbContext db, CancellationToken cancellationToken) =>
        db.Lines.Where(l => l.Kind == LineKind.Did).OrderBy(l => l.Key).Select(l => l.Key).FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// One new call on the replay, stamped half a minute ago, taking each kind in turn: a missed call to a ring group, the
    /// same caller again, a voicemail, an answered call, one Talk scored poorly, and one that hung up in the menu.
    /// </summary>
    public void AddCall()
    {
        var turn = _turn++;
        var at = clock.GetUtcNow().AddSeconds(-30);
        var t = At(at);
        var answered = At(at.AddSeconds(9));
        var ended = At(at.AddSeconds(40));
        var from = turn % 6 == 1 ? Callers[(turn - 1) / 6 % Callers.Length] : Callers[turn / 6 % Callers.Length];
        var group = directory.Current.Groups.FirstOrDefault(g => g.MemberList is { Count: > 0 });
        var member = group?.MemberList?[0] ?? "";
        var uuid = Guid.NewGuid().ToString();
        string Events(params string[] items) => string.Join(",", items);
        string Event(string time, string name, string data = "{}") => $$"""{"time":"{{time}}","event":"{{name}}","event_data":{{data}},"event_uuid":"{{Guid.NewGuid()}}"}""";

        var (status, extra, events) = (turn % 6) switch
        {
            // Missed: rang the group, nobody answered. The next turn is the same caller, trying again.
            0 or 1 => ("cancelled", group is null ? "" : $$""","to_group_id":"{{group.Id}}" """,
                Events(Event(t, "call_started"), Event(t, "seq_call_trying_endpoints"), Event(ended, "call_hangup"))),
            2 => ("accepted", "", Events(Event(t, "call_started"), Event(t, "seq_call_trying_endpoints"),
                Event(answered, "call_sent_to_voicemail", $$"""{"recipient_user_uuids":["{{member}}"]}"""),
                Event(ended, "vm_msg_recorded", $$"""{"recipient_user_uuids":["{{member}}"]}"""), Event(ended, "call_hangup"))),
            3 => ("accepted", member.Length == 0 ? "" : $$""","answered_by_user_uuid":"{{member}}" """,
                Events(Event(t, "call_started"), Event(t, "seq_call_trying_endpoints"), Event(answered, "call_accepted", AcceptedBy(member)), Event(ended, "call_hangup"))),
            4 => ("accepted", (member.Length == 0 ? "" : $$""","answered_by_user_uuid":"{{member}}" """) + ",\"quality_score\":42",
                Events(Event(t, "call_started"), Event(t, "seq_call_trying_endpoints"), Event(answered, "call_accepted", AcceptedBy(member)), Event(ended, "call_hangup"))),
            _ => ("accepted", ",\"to_smart_attendant_id\":45",
                Events(Event(t, "call_started", """{"to_smart_attendant_id":45}"""), Event(At(at.AddSeconds(12)), "call_hangup"))),
        };

        console.AddCall($$"""
            {"uuid":"{{uuid}}","time":"{{t}}","direction":"in","status":"{{status}}","duration":40,"from":"{{from}}","to":"+441144960042","country":"GB"{{extra}},
             "call_events":[{{events}}]}
            """);
        LogAdded(logger, turn % 6);
    }

    // Who answered, as Talk says it: by their extension, which names them.
    private string AcceptedBy(string? member) =>
        directory.Current.Users.FirstOrDefault(u => u.Uuid == member)?.Ext is { } ext ? $$"""{"accepted_by":"{{ext}}"}""" : "{}";

    private string? Member() => directory.Current.Groups.FirstOrDefault(g => g.MemberList is { Count: > 0 })?.MemberList?[0];

    private static string At(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo: the guest account could not be made: {Why}")]
    private static partial void LogGuestFailed(ILogger logger, string why);

    /// <summary>Calls in progress on the replay just now: the demo's stand-in for people on calls, which needs the live feed.</summary>
    public int InProgress() => _inProgress.Count;

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo: {Count} example flows set up, alerting {Admin} in TalkWatch.")]
    private static partial void LogSeeded(ILogger logger, int count, string admin);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Demo: a new call on the replay (kind {Kind}).")]
    private static partial void LogAdded(ILogger logger, int kind);
}

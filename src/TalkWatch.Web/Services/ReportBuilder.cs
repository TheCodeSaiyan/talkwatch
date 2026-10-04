using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Core.Calls;
using TalkWatch.Data;
using TalkWatch.Web.Components.Reports;

namespace TalkWatch.Web.Services;

/// <summary>A missed caller still to be got back to, as a report lists them.</summary>
public sealed record OpenCaller(string Who, int Times, DateTimeOffset Last, bool Voicemail);

/// <summary>Everything one copy of a report shows, worked out with its audience's access.</summary>
public sealed record ReportContent(
    string Name, DateTimeOffset From, DateTimeOffset To, TimeZoneInfo Zone, ReportSections Sections, string? Audience,
    CallStatistics? Stats, IReadOnlyList<OpenCaller> CallBacks, IReadOnlyList<SwitchboardStatistics> Switchboards, int CallCount, Uri? Link)
{
    /// <summary>The site's name, as the report's header and footer carry it.</summary>
    public string SiteName { get; init; } = "TalkWatch";

    /// <summary>The numbers the report covers, by name, or null for every number.</summary>
    public string? Covers { get; init; }

    /// <summary>The further parts the report holds, worked out only when it holds them.</summary>
    public ReportParts? Parts { get; init; }

    /// <summary>The same figures for the period before, when the report compares; null otherwise.</summary>
    public CallStatistics? Previous { get; init; }

    /// <summary>The hours it counts, in words ("Mon Tue Wed Thu Fri 09:00–17:30"); null for every hour.</summary>
    public string? Hours { get; init; }

    /// <summary>The headline figures for each number, when the report splits by number; null otherwise.</summary>
    public IReadOnlyList<(string Number, CallStatistics Stats)>? ByNumber { get; init; }
}

/// <summary>
/// Builds the copies of a report: one for each audience, which is each person it goes to and the owner of each email
/// channel it goes to, or the whole site for a site channel or a report with no recipients. Every figure is worked out
/// through that audience's access, so a report never shows anyone more than the app would.
/// </summary>
public sealed class ReportBuilder(IServiceScopeFactory scopes, IOptions<SiteOptions> site, ILoggerFactory loggers, TimeProvider clock, LineDirectorySync directory)
{
    public TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById(site.Value.TimeZone);

    /// <summary>Runs a report for the period that ends at the start of <paramref name="runAt"/>'s day, keeping a copy for each audience.</summary>
    public async Task<IReadOnlyList<ReportRun>> RunAsync(Guid reportId, DateTimeOffset runAt, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var report = await db.Reports.Include(r => r.Recipients).Include(r => r.Numbers).SingleAsync(r => r.Id == reportId, cancellationToken);
        var (from, to) = ReportTiming.PeriodOf(report, runAt, Zone);

        var channelIds = report.Recipients.Select(r => r.ChannelId).OfType<Guid>().ToList();
        var channels = await db.AlertChannels.Where(c => channelIds.Contains(c.Id)).ToListAsync(cancellationToken);
        var userIds = report.Recipients.Select(r => r.UserId).OfType<Guid>().ToList();
        var people = await db.Users.Where(u => userIds.Contains(u.Id)).ToListAsync(cancellationToken);
        var audiences = userIds.Select(id => (Guid?)id).Concat(channels.Select(c => c.OwnerUserId)).Distinct().ToList();
        if (audiences.Count == 0)
        {
            audiences.Add(null);
        }

        var runs = new List<ReportRun>();
        foreach (var audience in audiences)
        {
            runs.Add(await BuildAsync(report, audience, from, to, cancellationToken));
        }

        db.ReportRuns.AddRange(runs);

        // An email for each recipient, of the copy built for them: a person their own, a channel its owner's or the site's.
        ReportRun CopyFor(Guid? audience) => runs.Single(r => r.AudienceUserId == audience);
        foreach (var person in people)
        {
            var delivery = Delivery(CopyFor(person.Id), person.ReportAddress ?? "", person.UserName ?? "someone", runAt);
            delivery.UserId = person.Id;
            db.ReportDeliveries.Add(delivery);
        }

        foreach (var channel in channels)
        {
            db.ReportDeliveries.Add(Delivery(CopyFor(channel.OwnerUserId), channel.Target, channel.Name, runAt));
        }

        report.LastRunAt = runAt;
        await db.SaveChangesAsync(cancellationToken);
        return runs;
    }

    /// <summary>A person with no email address is recorded as not sent, so the report's page says why, rather than skipped.</summary>
    private static ReportDelivery Delivery(ReportRun run, string address, string recipient, DateTimeOffset now) => string.IsNullOrWhiteSpace(address)
        ? new ReportDelivery { Id = Guid.NewGuid(), SiteId = run.SiteId, RunId = run.Id, Address = "", Recipient = recipient, State = DeliveryState.Dead, NextAttemptAt = now, LastError = "No email address on their account." }
        : new ReportDelivery { Id = Guid.NewGuid(), SiteId = run.SiteId, RunId = run.Id, Address = address.Trim(), Recipient = recipient, State = DeliveryState.Pending, NextAttemptAt = now };

    private async Task<ReportRun> BuildAsync(Report report, Guid? audience, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var holder = scope.ServiceProvider.GetRequiredService<AccessScopeHolder>();
        var current = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        string? who = null;
        if (audience is { } userId)
        {
            // The person's own access: their role's permissions, then their grants through the database's filters.
            holder.UseSystemScope();
            who = await db.Users.Where(u => u.Id == userId).Select(u => u.UserName).SingleOrDefaultAsync(cancellationToken);
            var permissions = RolePermissions.Of(await (
                from ur in db.UserRoles
                join rc in db.RoleClaims on ur.RoleId equals rc.RoleId
                where ur.UserId == userId
                select new System.Security.Claims.Claim(rc.ClaimType!, rc.ClaimValue!)).ToListAsync(cancellationToken));
            holder.UseScope(AccessScope.ForUser(current.Id, userId, permissions));
        }
        else
        {
            holder.UseSystemScope();
        }

        // A report on some numbers: each copy only their calls, on top of whatever its reader may see.
        string? covers = null;
        if (report.Numbers.Count > 0)
        {
            var dids = report.Numbers.Select(n => n.Did).Order(StringComparer.Ordinal).ToArray();
            var names = await db.Lines.IgnoreQueryFilters().Where(l => l.SiteId == current.Id && l.Kind == LineKind.Did && dids.Contains(l.Key))
                .ToDictionaryAsync(l => l.Key, l => l.Name, cancellationToken);
            covers = string.Join(", ", dids.Select(d => names.GetValueOrDefault(d) is { Length: > 0 } name && name != d ? $"{name} ({d})" : d));
            holder.UseScope(holder.Resolve() with { Numbers = dids });
        }

        // Only some hours: every query counts just the calls in them. Worked out over the period, the one before it when
        // the report compares, and the last month, which the list still to call back looks over.
        if (report.Hours is { } hours)
        {
            var now = clock.GetUtcNow();
            var since = new[] { report.ComparePrevious ? from - (to - from) : from, now.AddDays(-31) }.Min().ToUniversalTime();
            var until = (to > now ? to : now).ToUniversalTime();
            var times = await db.Calls.AsNoTracking().Where(c => c.Time >= since && c.Time < until).Select(c => new { c.Id, c.Time }).ToListAsync(cancellationToken);
            holder.UseScope(holder.Resolve() with { OnlyCalls = [.. times.Where(c => hours.Matches(c.Time, Zone)).Select(c => c.Id)] });
        }

        var run = new ReportRun
        {
            Id = Guid.NewGuid(), SiteId = current.Id, ReportId = report.Id, AudienceUserId = audience, ReportName = report.Name,
            From = from, To = to, CreatedAt = clock.GetUtcNow(), Html = "",
        };

        var stats = report.Sections.HasFlag(ReportSections.Figures) || report.Sections.HasFlag(ReportSections.Lines) || report.Sections.HasFlag(ReportSections.BusiestTimes)
            ? await CallStatistics.ComputeAsync(db, from, to, Zone, cancellationToken)
            : null;
        var callBacks = report.Sections.HasFlag(ReportSections.CallBacks) ? await OpenCallersAsync(db, cancellationToken) : [];
        var switchboards = report.Sections.HasFlag(ReportSections.Switchboard)
            ? await SwitchboardStatistics.ComputeAsync(db, from, to, cancellationToken)
            : [];
        var callCount = 0;
        if (report.Sections.HasFlag(ReportSections.CallList))
        {
            var (start, end) = (from.ToUniversalTime(), to.ToUniversalTime());
            var calls = await db.Calls.AsNoTracking().Where(c => c.Time >= start && c.Time < end).OrderBy(c => c.Time).ToListAsync(cancellationToken);
            callCount = calls.Count;
            run.Csv = ExportEndpoints.Csv(calls, Zone);
        }

        var previous = report.ComparePrevious && stats is not null ? await CallStatistics.ComputeAsync(db, from - (to - from), from, Zone, cancellationToken) : null;

        // Each number's own figures: the copy's access narrowed to one number at a time, then put back.
        List<(string, CallStatistics)>? byNumber = null;
        if (report.SplitByNumber && report.Sections.HasFlag(ReportSections.Figures))
        {
            var whole = holder.Resolve();
            var lineNames = await db.Lines.IgnoreQueryFilters().Where(l => l.SiteId == current.Id && l.Kind == LineKind.Did).ToDictionaryAsync(l => l.Key, l => l.Name, cancellationToken);
            var dids = report.Numbers.Count > 0 ? report.Numbers.Select(n => n.Did).Order(StringComparer.Ordinal).ToList() : [.. lineNames.Keys.Order(StringComparer.Ordinal)];
            byNumber = [];
            foreach (var did in dids)
            {
                holder.UseScope(whole with { Numbers = [did] });
                byNumber.Add((lineNames.GetValueOrDefault(did) is { Length: > 0 } name && name != did ? $"{name} ({did})" : did,
                    await CallStatistics.ComputeAsync(db, from, to, Zone, cancellationToken)));
            }

            holder.UseScope(whole);
        }

        var further = ReportSections.People | ReportSections.MissedList | ReportSections.Callers | ReportSections.Sentiment
            | ReportSections.CallBackTimes | ReportSections.OutsideVoicemail | ReportSections.Alerts;
        var parts = (report.Sections & further) != 0
            ? await ReportParts.ComputeAsync(db, report.Sections, from, to, Zone, directory.Current.Contacts, report.Hours, cancellationToken)
            : null;

        var link = site.Value.PublicUrl is { } url ? new Uri(url, $"/reports/runs/{run.Id}") : null;
        run.Html = await RenderAsync(scope.ServiceProvider,
            new ReportContent(report.Name, from, to, Zone, report.Sections, who, stats, callBacks, switchboards, callCount, link)
            {
                SiteName = await db.Sites.Where(x => x.Id == current.Id).Select(x => x.Name).SingleOrDefaultAsync(cancellationToken) ?? "TalkWatch",
                Covers = covers,
                Parts = parts,
                Previous = previous,
                Hours = report.HoursDays is { } d && report.HoursFrom is { } hf && report.HoursTo is { } ht ? Components.Alerts.FlowText.Window(d, hf, ht, false) : null,
                ByNumber = byNumber,
            });
        return run;
    }

    /// <summary>Missed callers nobody has got back to, from the last 30 days, as the call-back list has them.</summary>
    private static async Task<IReadOnlyList<OpenCaller>> OpenCallersAsync(TalkWatchDbContext db, CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var open = await db.Calls.AsNoTracking().Returnable().Where(c => c.ReturnedAt == null && c.Time >= since)
            .Select(c => new { c.FromE164, c.FromRaw, c.CallerName, c.Time, c.Outcome }).ToListAsync(cancellationToken);
        var names = await CallerNames.ForAsync(db, open.Select(c => c.FromE164), cancellationToken);
        return [.. open.GroupBy(c => c.FromE164!)
            .Select(g =>
            {
                var latest = g.MaxBy(c => c.Time)!;
                return new OpenCaller(CallerNames.Who(latest.CallerName, latest.FromRaw, g.Key, names), g.Count(), latest.Time, g.Any(c => c.Outcome.IsVoicemail()));
            })
            .OrderByDescending(c => c.Last)];
    }

    private async Task<string> RenderAsync(IServiceProvider services, ReportContent content)
    {
        await using var renderer = new HtmlRenderer(services, loggers);
        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<ReportView>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(ReportView.Content)] = content }))).ToHtmlString());
    }
}

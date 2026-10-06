using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>
/// The database, always working for someone: the <see cref="AccessScope"/> it is created with filters every query on
/// call data, so a line grant is enforced wherever calls are read, not screen by screen.
/// </summary>
public sealed class TalkWatchDbContext(DbContextOptions<TalkWatchDbContext> options, IAccessScopeSource scope)
    : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    // The filters read these each time a query runs, so every context applies its own scope. They are properties of
    // the context because that is what EF parameterises per instance; a captured variable would be baked in. The
    // scope is asked for at query time, not construction: in a web request the context can be created during
    // authentication, before anyone is known to be signed in.
    public Guid ScopeSiteId => scope.Current.SiteId;
    public Guid? ScopeUserId => scope.Current.UserId;
    // What the scope's role allows, one property each so the query filters below can be parameterised by EF.
    public bool ScopeAllCalls => scope.Current.Can(Permission.AllCalls);
    public string? ScopeDid => scope.Current.Did;
    public string[]? ScopeNumbers => scope.Current.Numbers;
    public Guid[]? ScopeOnlyCalls => scope.Current.OnlyCalls;
    public bool ScopeAllAudio => scope.Current.Can(Permission.AllAudio);
    public bool ScopeAllTranscripts => scope.Current.Can(Permission.AllTranscripts);
    public bool ScopeReadAudit => scope.Current.Can(Permission.ReadAudit);
    public bool ScopeManageAlerts => scope.Current.Can(Permission.ManageAlerts);
    public bool ScopeManagePeople => scope.Current.Can(Permission.ManagePeople);
    public bool ScopeManageRetention => scope.Current.Can(Permission.ManageRetention);
    public bool ScopeManageReports => scope.Current.Can(Permission.ManageReports);

    /// <summary>Admins, and TalkWatch itself: the console's credentials and the way to it are for nobody else.</summary>
    public bool ScopeAdmin => scope.Current.Can(Permission.All);

    /// <summary>
    /// The keys that sign session cookies and antiforgery tokens. Kept here, not in the container, so a restart or
    /// redeploy does not sign everyone out, and they are backed up with everything else.
    /// </summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<Grant> Grants => Set<Grant>();
    public DbSet<AudioFile> AudioFiles => Set<AudioFile>();
    public DbSet<ConsoleVersionRecord> ConsoleVersions => Set<ConsoleVersionRecord>();
    public DbSet<LineRecord> Lines => Set<LineRecord>();
    public DbSet<AlertChannel> AlertChannels => Set<AlertChannel>();
    public DbSet<ContactLink> ContactLinks => Set<ContactLink>();
    public DbSet<AlertFlow> AlertFlows => Set<AlertFlow>();
    public DbSet<SwitchboardNodeRow> SwitchboardNodes => Set<SwitchboardNodeRow>();
    public DbSet<CallTranscript> CallTranscripts => Set<CallTranscript>();
    public DbSet<CallerName> CallerNames => Set<CallerName>();
    public DbSet<GroupMapping> GroupMappings => Set<GroupMapping>();
    public DbSet<ConsoleFact> ConsoleFacts => Set<ConsoleFact>();
    public DbSet<Report> Reports => Set<Report>();
    public DbSet<ReportRun> ReportRuns => Set<ReportRun>();
    public DbSet<ReportDelivery> ReportDeliveries => Set<ReportDelivery>();
    public DbSet<PushSubscriptionRow> PushSubscriptions => Set<PushSubscriptionRow>();
    public DbSet<AlertFlowRun> AlertFlowRuns => Set<AlertFlowRun>();
    public DbSet<AlertEvent> AlertEvents => Set<AlertEvent>();
    public DbSet<AlertDelivery> AlertDeliveries => Set<AlertDelivery>();
    public DbSet<AlertSettings> AlertSettings => Set<AlertSettings>();
    public DbSet<RetentionSettings> RetentionSettings => Set<RetentionSettings>();
    public DbSet<ConsoleSettings> ConsoleSettings => Set<ConsoleSettings>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Site> Sites => Set<Site>();
    public DbSet<CallRow> Calls => Set<CallRow>();
    public DbSet<CallEventRow> CallEvents => Set<CallEventRow>();
    public DbSet<CallLine> CallLines => Set<CallLine>();
    public DbSet<NumberRole> NumberRoles => Set<NumberRole>();
    public DbSet<AnsweringLine> AnsweringLines => Set<AnsweringLine>();
    public DbSet<CallFinding> CallFindings => Set<CallFinding>();
    public DbSet<NumberRoute> NumberRoutes => Set<NumberRoute>();
    public DbSet<ReportNumber> ReportNumbers => Set<ReportNumber>();
    public DbSet<RawPayload> RawPayloads => Set<RawPayload>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<AppUser>(e =>
        {
            e.HasIndex(u => u.SiteId);
            e.Property(u => u.ContextDid).HasMaxLength(32);
            e.Property(u => u.ReportEmail).HasMaxLength(256);
        });

        builder.Entity<Grant>(e =>
        {
            e.ToTable("grants");
            e.HasOne<Site>().WithMany().HasForeignKey(g => g.SiteId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(g => g.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(g => new { g.SiteId, g.UserId, g.Kind, g.Key, g.ByGroups }).IsUnique();
            e.Property(g => g.Groups).HasMaxLength(500);
            e.Property(g => g.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(g => g.Key).HasMaxLength(64);

            // People see their own grants; admins see everyone's in their site.
            e.HasQueryFilter(g => g.SiteId == ScopeSiteId && (ScopeManagePeople || g.UserId == ScopeUserId));
        });

        builder.Entity<NumberRole>(e =>
        {
            e.ToTable("number_roles");
            e.HasOne<Site>().WithMany().HasForeignKey(n => n.SiteId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<IdentityRole<Guid>>().WithMany().HasForeignKey(n => n.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(n => new { n.SiteId, n.UserId, n.Did }).IsUnique();
            e.HasIndex(n => new { n.SiteId, n.Did });
            e.Property(n => n.Did).HasMaxLength(32);

            // Who holds what is managed on the people and number pages, which check who may; the filters that let
            // the roles open calls each name the person themselves.
            e.HasQueryFilter(n => n.SiteId == ScopeSiteId);
        });

        builder.Entity<AnsweringLine>(e =>
        {
            e.ToTable("answering_lines");
            e.HasKey(a => new { a.SiteId, a.ContactId });
            e.HasOne<Site>().WithMany().HasForeignKey(a => a.SiteId);
            e.Property(a => a.ContactId).HasMaxLength(64);
            e.HasQueryFilter(a => a.SiteId == ScopeSiteId);
        });

        builder.Entity<CallFinding>(e =>
        {
            e.ToTable("call_findings");
            e.HasKey(f => f.CallId);
            e.HasOne<CallRow>().WithMany().HasForeignKey(f => f.CallId).OnDelete(DeleteBehavior.Cascade);
            e.Property(f => f.Finding).HasConversion<string>().HasMaxLength(24);
            e.Property(f => f.ContactId).HasMaxLength(64);
            e.Property(f => f.Phrase).HasMaxLength(200);
            e.Property(f => f.DecidedBy).HasMaxLength(256);
            // Only ever read through a call, which its own filter already limits; it holds no words the caller said.
            e.HasQueryFilter(f => f.SiteId == ScopeSiteId);
        });

        builder.Entity<NumberRoute>(e =>
        {
            e.ToTable("number_routes");
            e.HasKey(r => new { r.SiteId, r.Did, r.Kind, r.Key });
            e.HasIndex(r => new { r.SiteId, r.Kind, r.Key });
            e.Property(r => r.Did).HasMaxLength(32);
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Key).HasMaxLength(64);
            e.HasQueryFilter(r => r.SiteId == ScopeSiteId);
        });

        builder.Entity<Site>(e =>
        {
            e.ToTable("sites");
            e.Property(s => s.Name).HasMaxLength(200);
            e.Property(s => s.DefaultRegion).HasMaxLength(2);
        });

        builder.Entity<CallRow>(e =>
        {
            e.ToTable("calls");
            e.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId);
            e.HasIndex(c => new { c.SiteId, c.TalkUuid }).IsUnique();
            e.HasIndex(c => new { c.SiteId, c.Time });
            e.Property(c => c.TalkUuid).HasMaxLength(64);
            e.Property(c => c.Direction).HasMaxLength(16);
            e.Property(c => c.Status).HasMaxLength(32);
            e.Property(c => c.Outcome).HasConversion<string>().HasMaxLength(24);
            e.Property(c => c.ReturnedHow).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.ReturnedBy).HasMaxLength(100);
            // Returning a missed call looks for later calls to and from the same number.
            e.HasIndex(c => new { c.SiteId, c.FromE164 });
            e.HasIndex(c => new { c.SiteId, c.ToE164 });
            e.HasMany(c => c.Events).WithOne().HasForeignKey(v => v.CallId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(c => c.Lines).WithOne().HasForeignKey(l => l.CallId).OnDelete(DeleteBehavior.Cascade);

            // A call is visible when any line it touched is visible, and lines are filtered by grant below.
            // Then, when the person has chosen one number in the rail, only calls on that DID.
            // And for a report copy covering some numbers, only calls on them or through what they route to; covering only
            // chosen hours, only the calls in them.
            e.HasQueryFilter(c => c.SiteId == ScopeSiteId && (ScopeAllCalls || c.Lines.Any())
                && (ScopeOnlyCalls == null || ScopeOnlyCalls.Contains(c.Id))
                && (ScopeDid == null || c.Lines.Any(l => l.Kind == LineKind.Did && l.Key == ScopeDid))
                && (ScopeNumbers == null || c.Lines.Any(l => (l.Kind == LineKind.Did && ScopeNumbers.Contains(l.Key))
                    || Set<NumberRoute>().Any(r => ScopeNumbers.Contains(r.Did) && r.Kind == l.Kind && r.Key == l.Key))));
        });

        builder.Entity<CallEventRow>(e =>
        {
            e.ToTable("call_events");

            // Ids are set in code. Left to convention, EF takes a new event that already has an id, added to a call it
            // is tracking, for an existing row and UPDATEs it: nothing is updated and the save fails. That is what a
            // call first seen while ringing, then finished, does to every poll after it.
            e.Property(v => v.Id).ValueGeneratedNever();
            e.HasIndex(v => new { v.CallId, v.Sequence }).IsUnique();
            e.Property(v => v.Event).HasMaxLength(64);
            e.Property(v => v.DataJson).HasColumnType("jsonb");
            e.HasQueryFilter(v => v.SiteId == ScopeSiteId && (ScopeAllCalls || Set<CallLine>().Any(l => l.CallId == v.CallId))
                && (ScopeDid == null || Set<CallLine>().Any(l => l.CallId == v.CallId && l.Kind == LineKind.Did && l.Key == ScopeDid))
                && (ScopeNumbers == null || Set<CallLine>().Any(l => l.CallId == v.CallId && ((l.Kind == LineKind.Did && ScopeNumbers.Contains(l.Key))
                    || Set<NumberRoute>().Any(r => ScopeNumbers.Contains(r.Did) && r.Kind == l.Kind && r.Key == l.Key)))));
        });

        builder.Entity<CallLine>(e =>
        {
            e.ToTable("call_lines");
            e.HasKey(l => new { l.CallId, l.Kind, l.Key });
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.Key).HasMaxLength(64);

            // Grant resolution asks 'which calls touched these lines', so the index leads with the line.
            e.HasIndex(l => new { l.SiteId, l.Kind, l.Key });

            // Only granted lines are visible to non-admins, so a viewer granted a DID sees the call but not, say,
            // which other people it rang: least privilege, and it keeps the filters free of cycles. A role on a
            // number makes visible the lines that number covers: the DID, and what it routes calls through.
            e.HasQueryFilter(l => l.SiteId == ScopeSiteId
                && (ScopeAllCalls || Set<Grant>().Any(g => g.UserId == ScopeUserId && g.Kind == l.Kind && g.Key == l.Key)
                || Set<NumberRole>().Any(n => n.UserId == ScopeUserId
                    && ((l.Kind == LineKind.Did && l.Key == n.Did) || Set<NumberRoute>().Any(r => r.Did == n.Did && r.Kind == l.Kind && r.Key == l.Key)))));
        });

        builder.Entity<AudioFile>(e =>
        {
            e.ToTable("audio_files");
            e.HasOne<CallRow>().WithMany().HasForeignKey(a => a.CallId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.CallId, a.Kind }).IsUnique();
            e.HasIndex(a => new { a.SiteId, a.State });
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.State).HasConversion<string>().HasMaxLength(16);
            e.Property(a => a.RelativePath).HasMaxLength(260);
            e.Property(a => a.Sha256).HasMaxLength(64);
            e.Property(a => a.ContentType).HasMaxLength(100);
            e.Property(a => a.LastError).HasMaxLength(500);

            // Hearing a call is a bigger step than seeing it happened: visible only through a grant on one of the
            // call's lines that also allows this kind of audio. Separate flags for recordings and voicemail.
            e.HasQueryFilter(a => a.SiteId == ScopeSiteId && (ScopeAllAudio
                || Set<CallLine>().Any(l => l.CallId == a.CallId
                    && Set<Grant>().Any(g => g.UserId == ScopeUserId && g.Kind == l.Kind && g.Key == l.Key
                        && (a.Kind == AudioKind.Recording ? g.AllowRecordings : g.AllowVoicemail)))
                || Set<CallLine>().Any(l => l.CallId == a.CallId && Set<NumberRole>().Any(n => n.UserId == ScopeUserId
                    && ((l.Kind == LineKind.Did && l.Key == n.Did) || Set<NumberRoute>().Any(r => r.Did == n.Did && r.Kind == l.Kind && r.Key == l.Key))
                    && Set<IdentityRoleClaim<Guid>>().Any(c => c.RoleId == n.RoleId && c.ClaimType == Permissions.ClaimType && c.ClaimValue == nameof(Permission.AllAudio))))));
        });

        // What was said on a call: through a grant on one of its lines that allows transcripts, apart from seeing the
        // call or hearing it.
        builder.Entity<CallTranscript>(e =>
        {
            e.ToTable("call_transcripts");
            e.HasIndex(t => t.CallId).IsUnique();
            e.HasOne<CallRow>().WithMany().HasForeignKey(t => t.CallId).OnDelete(DeleteBehavior.Cascade);
            e.Property(t => t.TalkId).HasMaxLength(64);
            e.Property(t => t.SentimentClass).HasMaxLength(16);
            e.Property(t => t.Lines).HasColumnType("jsonb");
            e.HasQueryFilter(t => t.SiteId == ScopeSiteId && (ScopeAllTranscripts
                || Set<CallLine>().Any(l => l.CallId == t.CallId
                    && Set<Grant>().Any(g => g.UserId == ScopeUserId && g.Kind == l.Kind && g.Key == l.Key && g.AllowTranscripts))
                || Set<CallLine>().Any(l => l.CallId == t.CallId && Set<NumberRole>().Any(n => n.UserId == ScopeUserId
                    && ((l.Kind == LineKind.Did && l.Key == n.Did) || Set<NumberRoute>().Any(r => r.Did == n.Did && r.Kind == l.Kind && r.Key == l.Key))
                    && Set<IdentityRoleClaim<Guid>>().Any(c => c.RoleId == n.RoleId && c.ClaimType == Permissions.ClaimType && c.ClaimValue == nameof(Permission.AllTranscripts))))));
        });

        builder.Entity<AuditEvent>(e =>
        {
            e.ToTable("audit_events");
            e.HasIndex(a => new { a.SiteId, a.At });
            e.Property(a => a.Action).HasMaxLength(64);
            e.Property(a => a.TargetType).HasMaxLength(32);
            e.Property(a => a.TargetId).HasMaxLength(64);
            e.Property(a => a.Detail).HasMaxLength(500);

            // The audit log answers who heard what; only admins read it. Anyone's actions can be written to it.
            e.HasQueryFilter(a => a.SiteId == ScopeSiteId && ScopeReadAudit);
        });

        // Alerting is run by TalkWatch itself (system scope) and administered by admins, who see everything. A Manager
        // sees only their own: the channels they own, the flows they made, and the alerts sent to their channels. What
        // a channel's owner may hear of at all is decided when deliveries are made, by their grants.
        builder.Entity<AlertChannel>(e =>
        {
            e.ToTable("alert_channels");
            e.Property(c => c.Name).HasMaxLength(100);
            e.Property(c => c.ContactUuid).HasMaxLength(64);
            e.HasIndex(c => new { c.SiteId, c.ContactUuid });
            e.Property(c => c.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.Target).HasMaxLength(500);
            e.Property(c => c.ProtectedSecret).HasMaxLength(2000);
            e.HasQueryFilter(c => c.SiteId == ScopeSiteId && (ScopeManageAlerts || c.OwnerUserId == ScopeUserId));
        });

        builder.Entity<ContactLink>(e =>
        {
            e.ToTable("contact_links");
            e.HasKey(l => new { l.SiteId, l.ContactUuid, l.UserId });
            e.HasOne<Site>().WithMany().HasForeignKey(l => l.SiteId);
            e.HasOne<AppUser>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(l => new { l.SiteId, l.UserId });
            e.Property(l => l.ContactUuid).HasMaxLength(64);

            // Who carries which outside phone is managed by those who manage people, and resolved by the alerts in the system scope.
            e.HasQueryFilter(l => l.SiteId == ScopeSiteId && (ScopeManagePeople || l.UserId == ScopeUserId));
        });

        // Names for switchboards and menu options: configuration, not calls, so the whole site's are readable to anyone in it.
        // Names for numbers, from Talk's contacts: the site's address book, readable to anyone in it, as the names on
        // a call are.
        // What identity-provider groups give at sign-in: for people who manage people, and for TalkWatch itself.
        // What TalkWatch last saw of the console's account and settings: its own bookkeeping, read by nobody else.
        // Reports are set up by those who manage them; each copy is read by the person it was built for, or by them.
        builder.Entity<Report>(e =>
        {
            e.ToTable("reports");
            e.Property(r => r.Name).HasMaxLength(100);
            e.Property(r => r.Sections).HasConversion<int>();
            e.Property(r => r.Schedule).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Weekday).HasConversion<string>().HasMaxLength(16);
            e.Property(r => r.Cron).HasMaxLength(100);
            e.Property(r => r.Period).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(r => new { r.Enabled, r.NextRunAt });
            e.HasMany(r => r.Recipients).WithOne().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(r => r.Numbers).WithOne().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
            // Report managers see every report; someone who may set up reports on numbers sees those covering only them.
            e.HasQueryFilter(r => r.SiteId == ScopeSiteId && (ScopeManageReports || (Set<ReportNumber>().Any(n => n.ReportId == r.Id)
                && !Set<ReportNumber>().Any(n => n.ReportId == r.Id && !Set<NumberRole>().Any(nr => nr.UserId == ScopeUserId && nr.Did == n.Did
                    && Set<IdentityRoleClaim<Guid>>().Any(c => c.RoleId == nr.RoleId && c.ClaimType == Permissions.ClaimType && c.ClaimValue == nameof(Permission.ManageReports)))))));
        });

        builder.Entity<ReportNumber>(e =>
        {
            e.ToTable("report_numbers");
            e.HasKey(n => new { n.ReportId, n.Did });
            e.Property(n => n.Did).HasMaxLength(32);
        });

        builder.Entity<ReportRecipient>(e =>
        {
            e.ToTable("report_recipients");
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlertChannel>().WithMany().HasForeignKey(x => x.ChannelId).OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<ReportRun>(e =>
        {
            e.ToTable("report_runs");
            e.HasIndex(r => new { r.ReportId, r.CreatedAt });
            e.Property(r => r.ReportName).HasMaxLength(100);
            e.HasOne<Report>().WithMany().HasForeignKey(r => r.ReportId).OnDelete(DeleteBehavior.Cascade);
            // A copy is built for one person's access, or the whole site's: anyone else reads it only if they see every call,
            // and manage reports or the ones on their own numbers. Setting reports up is not a way to see more calls.
            e.HasQueryFilter(r => r.SiteId == ScopeSiteId && ((r.AudienceUserId != null && r.AudienceUserId == ScopeUserId)
                || (ScopeAllCalls && (ScopeManageReports || Set<Report>().Any(rep => rep.Id == r.ReportId)))));
        });

        // Emailing a copy: seen with the copy, so by the person it was for and by those who manage reports.
        // A browser that allowed notifications: its owner's, and visible to those who manage people.
        builder.Entity<PushSubscriptionRow>(e =>
        {
            e.ToTable("push_subscriptions");
            e.HasIndex(p => p.Endpoint).IsUnique();
            e.Property(p => p.Endpoint).HasMaxLength(1000);
            e.Property(p => p.P256dh).HasMaxLength(200);
            e.Property(p => p.Auth).HasMaxLength(100);
            e.HasOne<AppUser>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => p.SiteId == ScopeSiteId && (ScopeManagePeople || p.UserId == ScopeUserId));
        });

        builder.Entity<ReportDelivery>(e =>
        {
            e.ToTable("report_deliveries");
            e.HasIndex(d => new { d.State, d.NextAttemptAt });
            e.Property(d => d.Address).HasMaxLength(320);
            e.Property(d => d.Recipient).HasMaxLength(200);
            e.Property(d => d.State).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.LastError).HasMaxLength(500);
            e.HasOne<ReportRun>().WithMany().HasForeignKey(d => d.RunId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(d => d.SiteId == ScopeSiteId && (ScopeManageReports || Set<ReportRun>().Any(r => r.Id == d.RunId)));
        });

        builder.Entity<ConsoleFact>(e =>
        {
            e.ToTable("console_facts");
            e.HasKey(f => new { f.SiteId, f.Name });
            e.Property(f => f.Name).HasMaxLength(100);
            e.Property(f => f.Value).HasMaxLength(2000);
            e.HasQueryFilter(f => f.SiteId == ScopeSiteId && ScopeManageAlerts);
        });

        builder.Entity<GroupMapping>(e =>
        {
            e.ToTable("group_mappings");
            e.HasIndex(m => new { m.SiteId, m.Group }).IsUnique();
            e.Property(m => m.Group).HasMaxLength(200);
            e.Property(m => m.Role).HasMaxLength(256);
            e.HasMany(m => m.Lines).WithOne().HasForeignKey(l => l.MappingId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(m => m.SiteId == ScopeSiteId && ScopeManagePeople);
        });

        builder.Entity<GroupMappingLine>(e =>
        {
            e.ToTable("group_mapping_lines");
            e.HasIndex(l => new { l.MappingId, l.Kind, l.Key }).IsUnique();
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.Key).HasMaxLength(64);
        });

        builder.Entity<CallerName>(e =>
        {
            e.ToTable("caller_names");
            e.HasKey(n => new { n.SiteId, n.Number });
            e.Property(n => n.Number).HasMaxLength(32);
            e.Property(n => n.Name).HasMaxLength(200);
            e.HasQueryFilter(n => n.SiteId == ScopeSiteId);
        });

        builder.Entity<SwitchboardNodeRow>(e =>
        {
            e.ToTable("switchboard_nodes");
            e.HasKey(n => new { n.SiteId, n.NodeId });
            e.Property(n => n.NodeId).HasMaxLength(100);
            e.Property(n => n.Type).HasMaxLength(16);
            e.Property(n => n.Title).HasMaxLength(200);
            e.Property(n => n.ParentId).HasMaxLength(100);
            e.Property(n => n.Numbers).HasMaxLength(500);
            e.Property(n => n.GreetingFile).HasMaxLength(200);
            e.HasQueryFilter(n => n.SiteId == ScopeSiteId);
        });

        builder.Entity<AlertFlow>(e =>
        {
            e.ToTable("alert_flows");
            e.Property(f => f.Name).HasMaxLength(100);
            e.Property(f => f.Trigger).HasConversion<string>().HasMaxLength(32);
            e.Property(f => f.Definition).HasColumnType("jsonb");
            e.HasIndex(f => new { f.SiteId, f.Trigger });
            e.HasOne<AppUser>().WithMany().HasForeignKey(f => f.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(f => f.SiteId == ScopeSiteId && (ScopeManageAlerts || f.OwnerUserId == ScopeUserId));
        });

        // Run by TalkWatch itself; read by admins, and by a Manager for their own flows.
        builder.Entity<AlertFlowRun>(e =>
        {
            e.ToTable("alert_flow_runs");
            e.HasIndex(r => new { r.EventId, r.FlowId }).IsUnique();
            e.HasIndex(r => new { r.State, r.DueAt });
            e.Property(r => r.Plan).HasColumnType("jsonb");
            e.Property(r => r.State).HasConversion<string>().HasMaxLength(16);
            e.HasOne<AlertEvent>().WithMany().HasForeignKey(r => r.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlertFlow>().WithMany().HasForeignKey(r => r.FlowId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(r => r.SiteId == ScopeSiteId && (ScopeManageAlerts || Set<AlertFlow>().Any(f => f.Id == r.FlowId && f.OwnerUserId == ScopeUserId)));
        });

        builder.Entity<AlertEvent>(e =>
        {
            e.ToTable("alert_events");
            e.HasIndex(v => new { v.SiteId, v.Key }).IsUnique();
            e.Property(v => v.Type).HasConversion<string>().HasMaxLength(32);
            e.Property(v => v.Key).HasMaxLength(200);
            e.Property(v => v.UserUuid).HasMaxLength(64);
            e.Property(v => v.Title).HasMaxLength(200);
            e.Property(v => v.Message).HasMaxLength(1000);
            e.Property(v => v.AcknowledgedBy).HasMaxLength(100);
            e.HasQueryFilter(v => v.SiteId == ScopeSiteId && (ScopeManageAlerts
                || Set<AlertDelivery>().Any(d => d.EventId == v.Id && Set<AlertChannel>().Any(c => c.Id == d.ChannelId && c.OwnerUserId == ScopeUserId))));
        });

        builder.Entity<AlertDelivery>(e =>
        {
            e.ToTable("alert_deliveries");
            e.HasIndex(d => new { d.EventId, d.FlowId, d.ChannelId, d.Stage, d.Acknowledgement }).IsUnique();
            e.HasIndex(d => new { d.State, d.NextAttemptAt });
            e.HasOne<AlertEvent>().WithMany().HasForeignKey(d => d.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlertChannel>().WithMany().HasForeignKey(d => d.ChannelId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<AlertFlow>().WithMany().HasForeignKey(d => d.FlowId).OnDelete(DeleteBehavior.Cascade);
            e.Property(d => d.State).HasConversion<string>().HasMaxLength(16);
            e.Property(d => d.LastError).HasMaxLength(500);
            e.HasQueryFilter(d => d.SiteId == ScopeSiteId && (ScopeManageAlerts || Set<AlertChannel>().Any(c => c.Id == d.ChannelId && c.OwnerUserId == ScopeUserId)));
        });

        // Read by the token handler before anyone is signed in, so under the system scope; managed by owner and admins.
        builder.Entity<ApiToken>(e =>
        {
            e.ToTable("api_tokens");
            e.HasIndex(t => t.Hash).IsUnique();
            e.Property(t => t.Name).HasMaxLength(100);
            e.Property(t => t.Hash).HasMaxLength(64);
            e.HasOne<AppUser>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(t => t.SiteId == ScopeSiteId && (ScopeManagePeople || t.UserId == ScopeUserId));
        });

        builder.Entity<RetentionSettings>(e =>
        {
            e.ToTable("retention_settings");
            e.HasKey(r => r.SiteId);
            e.Property(r => r.Preset).HasConversion<string>().HasMaxLength(24);
            e.Ignore(r => r.Held);
            e.HasQueryFilter(r => r.SiteId == ScopeSiteId && ScopeManageRetention);
        });

        builder.Entity<AlertSettings>(e =>
        {
            e.ToTable("alert_settings");
            e.HasKey(a => a.SiteId);
            e.Property(a => a.SmtpHost).HasMaxLength(255);
            e.Property(a => a.SmtpFrom).HasMaxLength(320);
            e.Property(a => a.SmtpUsername).HasMaxLength(255);
            e.Property(a => a.SmtpProtectedPassword).HasMaxLength(2000);
            e.Property(a => a.TelegramProtectedBotToken).HasMaxLength(2000);
            e.Property(a => a.TelegramChatId).HasMaxLength(64);
            e.HasQueryFilter(a => a.SiteId == ScopeSiteId && ScopeManageAlerts);
        });

        builder.Entity<ConsoleSettings>(e =>
        {
            e.ToTable("console_settings");
            e.HasKey(c => c.SiteId);
            e.HasOne<Site>().WithMany().HasForeignKey(c => c.SiteId);
            e.Property(c => c.ConsoleUrl).HasMaxLength(255);
            e.Property(c => c.Username).HasMaxLength(255);
            e.Property(c => c.ProtectedPassword).HasMaxLength(2000);
            e.Property(c => c.CertificateSha256).HasMaxLength(100);
            e.Property(c => c.Route).HasConversion<string>().HasMaxLength(16);
            e.Property(c => c.ProtectedWireGuardConfig).HasMaxLength(8000);
            e.Property(c => c.ProtectedTailscaleAuthKey).HasMaxLength(2000);
            e.Property(c => c.TailscaleTags).HasMaxLength(500);
            e.HasQueryFilter(c => c.SiteId == ScopeSiteId && ScopeAdmin);
        });

        builder.Entity<LineRecord>(e =>
        {
            e.ToTable("lines");
            e.HasKey(l => new { l.SiteId, l.Kind, l.Key });
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(l => l.Key).HasMaxLength(64);
            e.Property(l => l.Name).HasMaxLength(200);
            e.Property(l => l.Ext).HasMaxLength(32);
            e.Property(l => l.SameAs).HasMaxLength(64);

            // Names of lines are themselves information: admins see all, others only the lines they are granted or
            // a number they hold a role on covers.
            e.HasQueryFilter(l => l.SiteId == ScopeSiteId
                && (ScopeAllCalls || Set<Grant>().Any(g => g.UserId == ScopeUserId && g.Kind == l.Kind && g.Key == l.Key)
                || Set<NumberRole>().Any(n => n.UserId == ScopeUserId
                    && ((l.Kind == LineKind.Did && l.Key == n.Did) || Set<NumberRoute>().Any(r => r.Did == n.Did && r.Kind == l.Kind && r.Key == l.Key)))));
        });

        builder.Entity<ConsoleVersionRecord>(e =>
        {
            e.ToTable("console_versions");
            e.HasIndex(v => new { v.SiteId, v.SeenAt });
            e.Property(v => v.UnifiOs).HasMaxLength(32);
            e.Property(v => v.UnifiOsChannel).HasMaxLength(32);
            e.Property(v => v.Talk).HasMaxLength(32);
            e.Property(v => v.TalkChannel).HasMaxLength(32);
            e.HasQueryFilter(v => v.SiteId == ScopeSiteId);
        });

        builder.Entity<RawPayload>(e =>
        {
            e.ToTable("raw_payloads");
            e.HasIndex(r => new { r.SiteId, r.Endpoint, r.FetchedAt });
            e.Property(r => r.Endpoint).HasMaxLength(200);
            e.Property(r => r.Body).HasColumnType("jsonb");

            // Raw payloads hold everything Talk sent, whatever it touched: only for someone who may see every call.
            e.HasQueryFilter(r => r.SiteId == ScopeSiteId && ScopeAllCalls);
        });
    }
}

/// <summary>Lets the EF tools build the model for migrations without a running database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<TalkWatchDbContext>
{
    public TalkWatchDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TalkWatchDbContext>().UseNpgsql("Host=design-time-only").Options, new FixedAccessScope(AccessScope.Nobody));
}

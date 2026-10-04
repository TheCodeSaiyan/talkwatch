using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Brings the database to the current schema, makes sure the site exists, and creates the first admin if nobody exists.
/// Runs before the app serves anything, so a failure stops start-up with the reason rather than half-working.
/// </summary>
public static partial class DatabaseStartup
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseStartup));

        await db.Database.MigrateAsync(cancellationToken);

        var siteOptions = scope.ServiceProvider.GetRequiredService<IOptions<SiteOptions>>().Value;
        var current = scope.ServiceProvider.GetRequiredService<CurrentSite>();
        var site = await db.Sites.OrderBy(s => s.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (site is null)
        {
            site = new Site { Id = Guid.NewGuid(), Name = siteOptions.Name, DefaultRegion = siteOptions.Region, CreatedAt = DateTimeOffset.UtcNow };
            db.Sites.Add(site);
            await db.SaveChangesAsync(cancellationToken);
            LogSiteCreated(logger, site.Name);
        }

        current.Id = site.Id;
        current.Region = site.DefaultRegion;

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var name in Roles.BuiltIn)
        {
            if (!await roles.RoleExistsAsync(name))
            {
                await roles.CreateAsync(new IdentityRole<Guid>(name));
            }

            await RolePermissions.SeedAsync(roles, (await roles.FindByNameAsync(name))!);
        }

        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.Users.AnyAsync(cancellationToken))
        {
            return;
        }

        var bootstrap = scope.ServiceProvider.GetRequiredService<IOptions<BootstrapOptions>>().Value;
        if (string.IsNullOrWhiteSpace(bootstrap.AdminUsername) || string.IsNullOrWhiteSpace(bootstrap.AdminPassword))
        {
            LogNoUsers(logger);
            return;
        }

        var admin = new AppUser { Id = Guid.NewGuid(), UserName = bootstrap.AdminUsername, SiteId = site.Id };
        var created = await users.CreateAsync(admin, bootstrap.AdminPassword);
        if (!created.Succeeded)
        {
            // Never log the password; the errors say what rule it broke.
            throw new InvalidOperationException("The bootstrap admin could not be created: " + string.Join(" ", created.Errors.Select(e => e.Description)));
        }

        await users.AddToRoleAsync(admin, Roles.Admin);
        LogAdminCreated(logger, admin.UserName);
    }

    /// <summary>
    /// The audio index against the files on disk (<see cref="AudioCheck"/>): at start-up by existence only, which is
    /// quick; with checksums from the admin page or 'check-audio'. Anything missing or damaged is copied again.
    /// </summary>
    public static async Task<AudioCheckResult> CheckAudioAsync(IServiceProvider services, bool checksums, CancellationToken cancellationToken)
    {
        using var scope = services.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        var result = await new AudioCheck(
            scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>(),
            scope.ServiceProvider.GetRequiredService<AudioStore>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>()).RunAsync(checksums, cancellationToken);
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseStartup));
        if (result.Clean)
        {
            LogAudioClean(logger, result.Checked);
        }
        else
        {
            LogAudioDrift(logger, result.Checked, result.Missing, result.Damaged, result.Orphans.Count);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Audio check: {Checked} copied files, all present.")]
    private static partial void LogAudioClean(ILogger logger, int @checked);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Audio check: {Checked} copied files; {Missing} missing and {Damaged} damaged, to be copied again; {Orphans} files nothing refers to, left in place.")]
    private static partial void LogAudioDrift(ILogger logger, int @checked, int missing, int damaged, int orphans);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created site {Site}.")]
    private static partial void LogSiteCreated(ILogger logger, string site);

    [LoggerMessage(Level = LogLevel.Warning, Message = "There are no users. Set Bootstrap__AdminUsername and Bootstrap__AdminPassword to create the first admin.")]
    private static partial void LogNoUsers(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Created the first admin, {User}. Bootstrap__AdminPassword can now be removed.")]
    private static partial void LogAdminCreated(ILogger logger, string user);
}

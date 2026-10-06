using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>The retention policy, for admins only. Every change is written to the audit log.</summary>
public static class RetentionEndpoints
{
    public sealed class PolicyForm
    {
        public RetentionPreset Preset { get; set; }

        // Used with Custom; a preset brings its own. Strings, because an empty box means 'keep'.
        public string? CallDays { get; set; }
        public string? AudioDays { get; set; }
        public string? MinimumDays { get; set; }
    }

    public static void MapRetention(this IEndpointRouteBuilder app)
    {
        // A 404 stays a 404, rather than being re-run through the not-found page, whose antiforgery check turns it into a 400.
        var retention = app.MapGroup("/admin/retention").RequireAuthorization(Permissions.Policy(Permission.ManageRetention)).WithMetadata(new SkipStatusCodePagesAttribute()).CheckFormToken();

        retention.MapPost("/", async ([FromForm] PolicyForm form, TalkWatchDbContext db, CurrentSite site, Audit audit, TimeProvider clock) =>
        {
            if (!Enum.IsDefined(form.Preset))
            {
                return Back("Choose a policy.");
            }

            int? callDays, audioDays, minimumDays;
            if (form.Preset == RetentionPreset.Custom)
            {
                if (!TryDays(form.CallDays, out callDays) || !TryDays(form.AudioDays, out audioDays) || !TryDays(form.MinimumDays, out minimumDays))
                {
                    return Back("Periods are whole numbers of days, or empty to keep.");
                }
            }
            else
            {
                (callDays, audioDays, minimumDays) = Retention.Of(form.Preset);
            }

            if (Retention.Problem(callDays, audioDays, minimumDays) is { } problem)
            {
                return Back(problem);
            }

            var settings = await db.RetentionSettings.SingleOrDefaultAsync();
            if (settings is null)
            {
                settings = new RetentionSettings { SiteId = site.Id };
                db.RetentionSettings.Add(settings);
            }

            (settings.Preset, settings.CallDays, settings.AudioDays, settings.MinimumDays, settings.UpdatedAt) =
                (form.Preset, callDays, audioDays, minimumDays, clock.GetUtcNow());
            await db.SaveChangesAsync();
            await audit.WriteAsync("retention.set", "retention", site.Id,
                $"{form.Preset}: calls {Describe(callDays)}, audio {Describe(audioDays)}, minimum {Describe(minimumDays)}");
            return Back("Policy saved. The next sweep applies it; it runs every six hours, or now from this page.");
        });

        retention.MapPost("/check-audio", async (IServiceProvider services, Audit audit, CurrentSite site, CancellationToken cancellationToken) =>
        {
            var result = await DatabaseStartup.CheckAudioAsync(services, checksums: true, cancellationToken);
            await audit.WriteAsync("audio.check", "audio", site.Id, $"{result.Checked} checked, {result.Missing} missing, {result.Damaged} damaged, {result.Orphans.Count} orphans");
            return Back(result.Clean
                ? $"All {result.Checked} copied files are present and match their checksums."
                : $"{result.Checked} copied files checked: {result.Missing} missing and {result.Damaged} damaged, to be copied again at the next poll; {result.Orphans.Count} files nothing refers to, left in place.");
        });

        retention.MapPost("/sweep", async (RetentionService service, Audit audit, CurrentSite site, CancellationToken cancellationToken) =>
        {
            var result = await service.RunOnceAsync(cancellationToken);
            await audit.WriteAsync("retention.sweep", "retention", site.Id, $"{result.Calls} calls, {result.Files} audio files");
            return Back($"Swept: {result.Calls} calls and {result.Files} audio files removed.");
        });
    }

    private static bool TryDays(string? text, out int? days)
    {
        days = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        if (int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            days = value;
            return true;
        }

        return false;
    }

    private static string Describe(int? days) => days is { } d ? $"{d} days" : "kept";

    private static IResult Back(string message) => Results.Redirect($"/admin/retention?msg={Uri.EscapeDataString(message)}");
}

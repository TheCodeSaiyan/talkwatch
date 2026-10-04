using System.Security.Claims;
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>Calls as CSV, for whoever is signed in: the same access scope as the calls page, so only their calls.</summary>
public static partial class ExportEndpoints
{
    public const int LongestPeriodDays = 366;

    private static readonly string[] Header =
        ["uuid", "time_utc", "time_local", "direction", "outcome", "status", "from", "from_e164", "caller_name", "to", "to_e164", "answered_by", "duration_seconds", "has_recording"];

    /// <summary>Calls as CSV, a header row first, every cell guarded against being read as a spreadsheet formula.</summary>
    public static string Csv(IEnumerable<CallRow> calls, TimeZoneInfo zone)
    {
        var csv = new StringBuilder().AppendJoin(',', Header).Append("\r\n");
        foreach (var c in calls)
        {
            string[] row =
            [
                c.TalkUuid, c.Time.ToString("o", CultureInfo.InvariantCulture), TimeZoneInfo.ConvertTime(c.Time, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                c.Direction, c.Outcome.ToString(), c.Status, c.FromRaw ?? "", c.FromE164 ?? "", c.CallerName ?? "", c.ToRaw ?? "", c.ToE164 ?? "",
                c.AnsweredByRaw ?? "", c.DurationSeconds.ToString(CultureInfo.InvariantCulture), c.HasRecording ? "true" : "false",
            ];
            csv.AppendJoin(',', row.Select(Cell)).Append("\r\n");
        }

        return csv.ToString();
    }

    public static void MapExport(this IEndpointRouteBuilder app)
    {
        // Whole days in the site's zone, as on the dashboard.
        app.MapGet("/calls/export.csv", async (DateOnly from, DateOnly to, HttpContext http, TalkWatchDbContext db, IOptions<SiteOptions> site, Audit audit) =>
        {
            // Who may export first: someone who may not learns nothing, not even that the period was wrong.
            if (await ExportableAsync(db, http) is not { } exportable)
            {
                return Results.Forbid();
            }

            if (Problem(from, to) is { } problem)
            {
                return problem;
            }

            var zone = TimeZoneInfo.FindSystemTimeZoneById(site.Value.TimeZone);
            var calls = await CallsAsync(exportable, from, to, zone);

            var csv = Csv(calls, zone);

            await audit.WriteAsync("calls.export", "calls", Guid.Empty, $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}, {calls.Count} calls");
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray(), "text/csv; charset=utf-8",
                $"talkwatch-calls-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.csv");
        }).RequireAuthorization();

        // The same calls as Parquet, with typed columns, for analysis tools. Not opened by a spreadsheet as cells, so no
        // formula guard is needed.
        app.MapGet("/calls/export.parquet", async (DateOnly from, DateOnly to, HttpContext http, TalkWatchDbContext db, IOptions<SiteOptions> site, Audit audit) =>
        {
            // Who may export first: someone who may not learns nothing, not even that the period was wrong.
            if (await ExportableAsync(db, http) is not { } exportable)
            {
                return Results.Forbid();
            }

            if (Problem(from, to) is { } problem)
            {
                return problem;
            }

            var zone = TimeZoneInfo.FindSystemTimeZoneById(site.Value.TimeZone);
            var calls = await CallsAsync(exportable, from, to, zone);
            var rows = calls.Select(c => new ParquetRow
            {
                Uuid = c.TalkUuid, TimeUtc = c.Time.UtcDateTime,
                TimeLocal = TimeZoneInfo.ConvertTime(c.Time, zone).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                Direction = c.Direction, Outcome = c.Outcome.ToString(), Status = c.Status, From = c.FromRaw, FromE164 = c.FromE164,
                CallerName = c.CallerName, To = c.ToRaw, ToE164 = c.ToE164, AnsweredBy = c.AnsweredByRaw,
                DurationSeconds = c.DurationSeconds, HasRecording = c.HasRecording,
            }).ToList();

            using var file = new MemoryStream();
            await Parquet.Serialization.ParquetSerializer.SerializeAsync(rows, file);
            await audit.WriteAsync("calls.export", "calls", Guid.Empty, $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd}, {calls.Count} calls, Parquet");
            http.Response.Headers.CacheControl = "no-store";
            return Results.File(file.ToArray(), "application/vnd.apache.parquet", $"talkwatch-calls-{from:yyyy-MM-dd}-to-{to:yyyy-MM-dd}.parquet");
        }).RequireAuthorization();
    }

    /// <summary>One call as a Parquet row: the CSV's columns, with times, numbers and flags typed.</summary>
    public sealed class ParquetRow
    {
        [System.Text.Json.Serialization.JsonPropertyName("uuid")] public string Uuid { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("time_utc")] public DateTime TimeUtc { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("time_local")] public string TimeLocal { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("direction")] public string Direction { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("outcome")] public string Outcome { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("status")] public string Status { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("from")] public string? From { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("from_e164")] public string? FromE164 { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("caller_name")] public string? CallerName { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("to")] public string? To { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("to_e164")] public string? ToE164 { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("answered_by")] public string? AnsweredBy { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("duration_seconds")] public int DurationSeconds { get; set; }
        [System.Text.Json.Serialization.JsonPropertyName("has_recording")] public bool HasRecording { get; set; }
    }

    private static IResult? Problem(DateOnly from, DateOnly to) =>
        to < from || to.DayNumber - from.DayNumber >= LongestPeriodDays
            ? Results.BadRequest($"Give from and to as yyyy-MM-dd, to on or after from, at most {LongestPeriodDays} days.")
            : null;

    /// <summary>
    /// The calls this person may export: every call they can see with Export site-wide, or else only those on the
    /// numbers where a role of theirs allows it. Null when neither, which is a 403.
    /// </summary>
    private static async Task<IQueryable<CallRow>?> ExportableAsync(TalkWatchDbContext db, HttpContext http)
    {
        if (http.User.Can(Permission.Export))
        {
            return db.Calls;
        }

        if (!Guid.TryParse(http.User.FindFirstValue(System.Security.Claims.ClaimTypes.NameIdentifier), out var me)
            || !await NumberAccess.AnywhereAsync(db, me, Permission.Export))
        {
            return null;
        }

        return db.Calls.OnNumbers(db, NumberAccess.NumbersWith(db, me, Permission.Export));
    }

    /// <summary>The signed-in person's calls on whole days in the site's zone, through their access scope.</summary>
    private static async Task<List<CallRow>> CallsAsync(IQueryable<CallRow> exportable, DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var (startLocal, endLocal) = (from.ToDateTime(TimeOnly.MinValue), to.ToDateTime(TimeOnly.MinValue).AddDays(1));
        var start = new DateTimeOffset(startLocal, zone.GetUtcOffset(startLocal)).ToUniversalTime();
        var end = new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)).ToUniversalTime();
        return await exportable.AsNoTracking().Where(c => c.Time >= start && c.Time < end).OrderBy(c => c.Time).ToListAsync();
    }

    /// <summary>
    /// One CSV cell. Text a spreadsheet would run as a formula (=, +, -, @, tab or return first) is given a leading
    /// quote mark, since caller names come from outside; a plain phone number such as +441144960042 is left as it is.
    /// </summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0], StringComparison.Ordinal) && !PhoneNumber().IsMatch(value))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : value;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^\+[0-9 ]{3,20}$")]
    private static partial System.Text.RegularExpressions.Regex PhoneNumber();
}

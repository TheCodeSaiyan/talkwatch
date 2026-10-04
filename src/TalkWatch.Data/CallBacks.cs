using Microsoft.EntityFrameworkCore;
using TalkWatch.Core.Calls;

namespace TalkWatch.Data;

/// <summary>How a missed caller was got back to.</summary>
public enum CallBackHow
{
    /// <summary>Someone called the number back, whether or not they answered: the call was returned.</summary>
    CalledBack,

    /// <summary>The caller rang again and that call was answered.</summary>
    GotThrough,

    /// <summary>Someone marked it done in TalkWatch: dealt with some other way, or not worth returning.</summary>
    MarkedDone,
}

/// <summary>
/// Missed calls waiting to be returned. A missed or voicemail call from a number that reads as one is open until a
/// later outbound call goes to that number, a later call from it is answered, or someone marks it done. That is kept
/// on the call, not worked out for each viewer, because the call that returned it may be on a line they cannot see.
/// </summary>
public static class CallBacks
{
    /// <summary>
    /// Marks every open missed call that a later call has returned, with the first such call's time. One statement,
    /// so it runs after every poll and also as the backfill when the columns arrive.
    /// </summary>
    public const string MarkReturned = """
        UPDATE calls m SET "ReturnedAt" = r."Time", "ReturnedHow" = CASE WHEN r."Direction" = 'out' THEN 'CalledBack' ELSE 'GotThrough' END
        FROM calls m2 CROSS JOIN LATERAL (
            SELECT o."Time", o."Direction" FROM calls o
            WHERE o."SiteId" = m2."SiteId" AND o."Time" > m2."Time"
              AND ((o."Direction" = 'out' AND o."ToE164" = m2."FromE164")
                OR (o."Direction" = 'in' AND o."Outcome" = 'Answered' AND o."FromE164" = m2."FromE164"))
            ORDER BY o."Time" LIMIT 1) r
        WHERE m."Id" = m2."Id" AND m2."Direction" = 'in' AND m2."Outcome" IN ('Missed', 'Voicemail')
          AND m2."FromE164" IS NOT NULL AND m2."ReturnedAt" IS NULL
        """;

    /// <summary>Marks what later calls have returned, on one site. Returns how many calls it marked.</summary>
    public static Task<int> MarkReturnedAsync(TalkWatchDbContext db, Guid siteId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlRawAsync(MarkReturned + """ AND m2."SiteId" = {0}""", [siteId], cancellationToken);

    /// <summary>The calls a caller could be rung back about: missed or voicemail, from a number that reads as one.</summary>
    public static IQueryable<CallRow> Returnable(this IQueryable<CallRow> calls) =>
        calls.Where(c => c.Direction == "in" && CallOutcomes.UnansweredKinds.Contains(c.Outcome) && c.FromE164 != null);
}

using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// Emails report copies waiting to go, through the mail server alerts use. The report's HTML is the body, with a line
/// of plain text for clients that show no HTML, and the call list is attached where the report has one. A failure is
/// retried after 5 and 15 minutes, then 1 and 6 hours, and given up after <see cref="MaxAttempts"/>.
/// </summary>
public sealed partial class ReportMailer(IServiceScopeFactory scopes, ReportBuilder builder, TimeProvider clock, ILogger<ReportMailer> logger,
    Microsoft.Extensions.Options.IOptions<DemoOptions> demo, Microsoft.Extensions.Options.IOptions<SiteOptions> site, MailSender mailer) : IDisposable
{
    public const int MaxAttempts = 5;

    private static readonly TimeSpan[] BackOff = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Sends every copy due now. Returns how many went.</summary>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
            var db = scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>();
            var now = clock.GetUtcNow();
            var due = await db.ReportDeliveries.Where(d => d.State == DeliveryState.Pending && d.NextAttemptAt <= now)
                .OrderBy(d => d.NextAttemptAt).Take(20).ToListAsync(cancellationToken);

            // A demo mails nothing: the copies stay readable in TalkWatch, and each says why it was not sent.
            if (demo.Value.Enabled)
            {
                foreach (var delivery in due)
                {
                    (delivery.State, delivery.LastError) = (DeliveryState.Cancelled, "The demo sends no email; the report is here to read.");
                }

                await db.SaveChangesAsync(cancellationToken);
                return 0;
            }

            var sent = 0;
            foreach (var delivery in due)
            {
                // A person's copy goes where their reports go as it is now (their reporting address, else their email):
                // one corrected since the report ran is the one used.
                if (delivery.UserId is { } person)
                {
                    var to = await db.Users.Where(u => u.Id == person).Select(u => new { u.ReportEmail, u.Email }).SingleOrDefaultAsync(cancellationToken);
                    var email = to is null ? null : AppUser.ReportTo(to.ReportEmail, to.Email);
                    if (string.IsNullOrEmpty(email))
                    {
                        (delivery.State, delivery.LastError) = (DeliveryState.Dead, "No email address on their account.");
                        await db.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    delivery.Address = email;
                }

                var run = await db.ReportRuns.AsNoTracking().SingleAsync(r => r.Id == delivery.RunId, cancellationToken);
                delivery.Attempts++;
                try
                {
                    await SendAsync(delivery.Address, run, cancellationToken);
                    (delivery.State, delivery.SentAt, delivery.LastError) = (DeliveryState.Sent, clock.GetUtcNow(), null);
                    sent++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031 // A copy that cannot go is recorded and retried; nothing here may stop TalkWatch.
                catch (Exception e)
#pragma warning restore CA1031
                {
                    delivery.LastError = e.Message.Length > 500 ? e.Message[..500] : e.Message;
                    if (delivery.Attempts >= MaxAttempts)
                    {
                        delivery.State = DeliveryState.Dead;
                        LogGaveUp(logger, run.ReportName, e);
                    }
                    else
                    {
                        delivery.NextAttemptAt = clock.GetUtcNow() + BackOff[Math.Min(delivery.Attempts - 1, BackOff.Length - 1)];
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            return sent;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SendAsync(string to, ReportRun run, CancellationToken cancellationToken)
    {
        var options = await mailer.OptionsAsync(cancellationToken);

        var from = TimeZoneInfo.ConvertTime(run.From, builder.Zone);
        var last = TimeZoneInfo.ConvertTime(run.To.AddTicks(-1), builder.Zone);
        var period = from.Date == last.Date ? $"{from:ddd d MMM yyyy}" : $"{from:d MMM} to {last:d MMM yyyy}";

        // From TalkWatch by name, at the configured address, so it reads as the report it is in an inbox.
        using var mail = new MailMessage(new MailAddress(options.From, "TalkWatch"), new MailAddress(to))
        {
            Subject = $"{run.ReportName}: {period}", BodyEncoding = Encoding.UTF8, SubjectEncoding = Encoding.UTF8,
        };
        var page = site.Value.PublicUrl is { } url ? new Uri(url, $"/reports/runs/{run.Id}") : null;
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            $"{run.ReportName}\n{period}\n\nThis report is laid out for a mail client that shows HTML. "
            + (page is null ? "It is also in TalkWatch, under Reports." : $"Read it in TalkWatch: {page}") + "\n\nSent by TalkWatch.",
            Encoding.UTF8, MediaTypeNames.Text.Plain));
        // A whole document around the kept report: light, on the report's own ground, scaled for a phone.
        mail.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + "<meta name=\"color-scheme\" content=\"light\"><meta name=\"supported-color-schemes\" content=\"light\">"
            + $"<title>{WebUtility.HtmlEncode(run.ReportName)}</title></head><body style=\"margin: 0; padding: 0; background: #eef1f4;\">{run.Html}</body></html>",
            Encoding.UTF8, MediaTypeNames.Text.Html));
        if (run.Csv is { } csv)
        {
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            var name = $"calls-{run.From:yyyy-MM-dd}-to-{run.To:yyyy-MM-dd}.csv";
            var attachment = new Attachment(new MemoryStream(bytes), name, "text/csv");
            // The disposition's file name is what mail clients save it as.
            attachment.ContentDisposition!.FileName = name;
            mail.Attachments.Add(attachment);
        }

        await mailer.SendAsync(options, mail, cancellationToken);
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Gave up emailing the report '{Report}' after the last retry.")]
    private static partial void LogGaveUp(ILogger logger, string report, Exception exception);
}

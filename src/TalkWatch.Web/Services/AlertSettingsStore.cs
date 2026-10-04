using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// The mail server and Telegram defaults alerts go out with: what an admin saved on the alerts page, field by field,
/// over the Smtp__ and Telegram__ settings. Read fresh each time, so a change applies to the next alert sent.
/// </summary>
public sealed class AlertSettingsStore(IServiceScopeFactory scopes, ChannelSecrets secrets, IOptions<SmtpOptions> smtp, IOptions<TelegramOptions> telegram)
{
    public SmtpOptions SmtpFromSettings => smtp.Value;

    public TelegramOptions TelegramFromSettings => telegram.Value;

    /// <summary>What was saved on the page, or null when nothing has been.</summary>
    public async Task<AlertSettings?> SavedAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<AccessScopeHolder>().UseSystemScope();
        return await scope.ServiceProvider.GetRequiredService<TalkWatchDbContext>().AlertSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<SmtpOptions> SmtpAsync(CancellationToken cancellationToken)
    {
        var saved = await SavedAsync(cancellationToken);
        var env = smtp.Value;
        return new SmtpOptions
        {
            Host = saved?.SmtpHost ?? env.Host,
            Port = saved?.SmtpPort ?? env.Port,
            From = saved?.SmtpFrom ?? env.From,
            Username = saved?.SmtpUsername ?? env.Username,
            Password = secrets.Unprotect(saved?.SmtpProtectedPassword) ?? env.Password,
            StartTls = saved?.SmtpStartTls ?? env.StartTls,
        };
    }

    public async Task<TelegramOptions> TelegramAsync(CancellationToken cancellationToken)
    {
        var saved = await SavedAsync(cancellationToken);
        var env = telegram.Value;
        return new TelegramOptions
        {
            BotToken = secrets.Unprotect(saved?.TelegramProtectedBotToken) ?? env.BotToken,
            ChatId = saved?.TelegramChatId ?? env.ChatId,
        };
    }
}

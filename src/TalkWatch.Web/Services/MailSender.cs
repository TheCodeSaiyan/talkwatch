using System.Net.Mail;
using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace TalkWatch.Web.Services;

/// <summary>
/// Sends a message through the mail server in the settings, for alerts, reports and the test email alike. TalkWatch
/// greets the server by its public host name (or the From address's domain), not the machine's: in a container that
/// is a bare id, which strict servers refuse as an invalid HELO name. Port 465 speaks TLS from the first byte; any
/// other port uses STARTTLS when the settings say so. A refusal says which step failed and the server's own words.
/// </summary>
public sealed class MailSender(AlertSettingsStore settings, IOptions<SiteOptions> site)
{
    /// <summary>How long a mail server has to answer each step.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The mail server settings, or why email cannot be sent with them: not set up, or a port that is for reading mail.
    /// Throws <see cref="InvalidOperationException"/> with the reason.
    /// </summary>
    public async Task<MailServer> OptionsAsync(CancellationToken cancellationToken)
    {
        var options = await settings.SmtpAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.From))
        {
            throw new InvalidOperationException("Email is not set up: give a mail server and a From address in the alert settings, or set Smtp__Host and Smtp__From.");
        }

        // A port that cannot be a mail server's would only time out; say why at once instead.
        if (SmtpOptions.PortProblem(options.Port) is { } why)
        {
            throw new InvalidOperationException(why);
        }

        return new MailServer(options.Host, options.Port, options.From, options.Username, options.Password, options.StartTls);
    }

    /// <summary>The name TalkWatch greets the mail server with: its public host, else the From address's domain.</summary>
    public string GreetingName(MailServer options) =>
        site.Value.PublicUrl?.Host is { Length: > 0 } host ? host
        : MailAddress.TryCreate(options.From, out var from) ? from.Host
        : "localhost";

    /// <summary>
    /// Sends the message. Any failure is an <see cref="SmtpException"/> saying what went wrong in words an administrator
    /// can act on, so the callers' retry and give-up handling stays as it was.
    /// </summary>
    public async Task SendAsync(MailServer options, MailMessage mail, CancellationToken cancellationToken)
    {
        using var message = MimeMessage.CreateFromMailMessage(mail);
        // Revocation is not checked, as the mail client before this never did: a container often cannot reach the lists.
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds, LocalDomain = GreetingName(options), CheckCertificateRevocation = false };
        var security = options.Port == 465 ? SecureSocketOptions.SslOnConnect : options.StartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None;
        try
        {
            await client.ConnectAsync(options.Host, options.Port, security, cancellationToken);
            if (!string.IsNullOrEmpty(options.Username))
            {
                // Most servers offer sign-in only once the connection is encrypted.
                if (!client.Capabilities.HasFlag(SmtpCapabilities.Authentication))
                {
                    throw new SmtpException(security == SecureSocketOptions.None
                        ? $"The mail server {options.Host} offers no sign-in on an unencrypted connection: turn STARTTLS on, or clear the username if it needs none."
                        : $"The mail server {options.Host} offers no sign-in: clear the username if it needs none.");
                }

                await client.AuthenticateAsync(options.Username, options.Password ?? "", cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SmtpException)
        {
            throw;
        }
        // Whatever else the mail library throws is a failed send to report, never an error to stop TalkWatch with.
        catch (Exception e)
        {
            throw new SmtpException(Describe(e, options) ?? $"Sending through {options.Host} failed: {e.Message}", e);
        }
    }

    // What went wrong, by the step it went wrong at; null for anything that is not a mail failure.
    private static string? Describe(Exception e, MailServer options) => e switch
    {
        SmtpCommandException { ErrorCode: SmtpErrorCode.RecipientNotAccepted } c => $"The mail server refused the recipient {c.Mailbox}: {c.Message} ({(int)c.StatusCode})",
        SmtpCommandException { ErrorCode: SmtpErrorCode.SenderNotAccepted } c => $"The mail server refused the From address {c.Mailbox}: {c.Message} ({(int)c.StatusCode})",
        SmtpCommandException { ErrorCode: SmtpErrorCode.MessageNotAccepted } c => $"The mail server refused the message: {c.Message} ({(int)c.StatusCode})",
        SmtpCommandException c => $"The mail server refused: {c.Message} ({(int)c.StatusCode})",
        AuthenticationException => $"The mail server did not accept the username and password for {options.Username}.",
        SslHandshakeException => $"TLS with {options.Host} on port {options.Port} failed: {e.Message.Split('\n')[0]}",
        SmtpProtocolException => $"The mail server on {options.Host} port {options.Port} did not answer as a mail server: {e.Message}",
        SocketException or IOException or TimeoutException or OperationCanceledException => $"Could not reach the mail server {options.Host} on port {options.Port}: {e.Message}",
        ServiceNotConnectedException or ServiceNotAuthenticatedException => e.Message,
        _ => null,
    };
}

/// <summary>Mail server settings checked to be usable: a host and a From address, on a port that may be a mail server's.</summary>
public sealed record MailServer(string Host, int Port, string From, string? Username, string? Password, bool StartTls);

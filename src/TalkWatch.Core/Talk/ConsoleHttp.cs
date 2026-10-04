using System.Net;
using System.Net.Security;
using System.Security.Cryptography;

namespace TalkWatch.Core.Talk;

public static class ConsoleHttp
{
    /// <summary>
    /// A handler for talking to a console. Certificates that validate normally are accepted; a self-signed console
    /// certificate is accepted only when its SHA-256 matches <paramref name="pinnedSha256"/>. Without a pin, a
    /// self-signed certificate is refused: TalkWatch never turns certificate checks off.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? pinnedSha256)
    {
        var handler = new SocketsHttpHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        if (!string.IsNullOrWhiteSpace(pinnedSha256))
        {
            var pin = pinnedSha256.Replace(":", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None
                || (certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == pin);
        }

        return handler;
    }
}

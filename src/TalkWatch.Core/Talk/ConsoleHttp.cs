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
    public static HttpMessageHandler CreateHandler(string? pinnedSha256) => CreateHandler(pinnedSha256, proxy: null, new CookieContainer());

    /// <summary>
    /// The same, reaching the console through <paramref name="proxy"/> (a SOCKS5 proxy into a tunnel to the console's
    /// network) when it is given, and keeping the session's cookie in <paramref name="cookies"/>, so a handler rebuilt for
    /// a new proxy keeps the session.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? pinnedSha256, Uri? proxy, CookieContainer cookies)
    {
        var handler = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            // Never the machine's own proxy settings: the console is reached directly, or through the tunnel.
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy),
        };
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

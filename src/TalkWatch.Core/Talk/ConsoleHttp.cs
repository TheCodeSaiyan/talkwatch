using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TalkWatch.Core.Talk;

public static class ConsoleHttp
{
    /// <summary>
    /// A handler for talking to a console. With a pin, only the certificate whose SHA-256 is <paramref name="pinnedSha256"/>
    /// is accepted, self-signed as consoles' are; without one, only a certificate that validates normally. TalkWatch never
    /// turns certificate checks off.
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
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) => Trusts(certificate, errors, pinnedSha256);
        }

        return handler;
    }

    /// <summary>
    /// Whether to trust the console's certificate. A pin is the trust: the pinned certificate, and no other, even one a
    /// public authority vouches for, since anyone can have one of those for a console reached by name. Without a pin,
    /// a certificate that validates normally.
    /// </summary>
    public static bool Trusts(X509Certificate? certificate, SslPolicyErrors errors, string? pinnedSha256) =>
        string.IsNullOrWhiteSpace(pinnedSha256)
            ? errors == SslPolicyErrors.None
            : certificate is not null && string.Equals(Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())),
                pinnedSha256.Replace(":", "", StringComparison.Ordinal).Trim(), StringComparison.OrdinalIgnoreCase);
}

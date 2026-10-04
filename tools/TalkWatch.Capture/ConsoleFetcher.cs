using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TalkWatch.Capture;

/// <summary>
/// Signs in to a UniFi OS console with a local account and fetches the given paths, recording each response.
/// </summary>
/// <remarks>
/// The sign-in shape (POST /api/auth/login, a session cookie, and a CSRF token header echoed back) is how UniFi OS
/// consoles are generally reported to work; the discovery spike is what confirms it for Talk. The password,
/// cookie and token are held only in this object and never recorded.
/// </remarks>
public sealed class ConsoleFetcher : IDisposable
{
    private static readonly string[] CsrfHeaders = ["X-Updated-Csrf-Token", "X-Csrf-Token"];

    private readonly HttpClient _http;
    private string? _csrfToken;

    public ConsoleFetcher(Uri console, HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { BaseAddress = console, Timeout = TimeSpan.FromMinutes(2) };
    }

    /// <summary>
    /// A handler that trusts the console's certificate only if its SHA-256 matches <paramref name="pinnedSha256"/>.
    /// Consoles ship a self-signed certificate, so pinning is the safe alternative to turning validation off.
    /// </summary>
    public static HttpMessageHandler CreateHandler(string? pinnedSha256)
    {
        var handler = new SocketsHttpHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        if (pinnedSha256 is not null)
        {
            var pin = pinnedSha256.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();
            handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                errors == SslPolicyErrors.None
                || (certificate is not null && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == pin);
        }

        return handler;
    }

    /// <summary>The SHA-256 of the certificate a host presents, for the user to compare against the console before pinning it.</summary>
    public static async Task<string> ReadCertificateSha256Async(Uri console, CancellationToken cancellationToken)
    {
        X509Certificate? seen = null;
        using var handler = new SocketsHttpHandler();
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
        {
            seen = certificate is null ? null : new X509Certificate2(certificate);
            return false;
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        Exception? failure = null;
        try
        {
            using var _ = await http.GetAsync(console, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Expected once a certificate has been seen, because the callback refuses it. Otherwise the host was not reached.
            failure = e;
        }

        return seen is not null
            ? Convert.ToHexString(SHA256.HashData(seen.GetRawCertData()))
            : throw new InvalidOperationException(failure is TaskCanceledException
                ? $"{console.Host} did not answer within 15 seconds. Check the address: it is the console's LAN address, usually the network's gateway."
                : $"{console.Host} could not be reached: {failure?.GetBaseException().Message ?? "no certificate was presented"}");
    }

    public async Task SignInAsync(string username, string password, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync("/api/auth/login",
            new { username, password, rememberMe = false }, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Sign-in was refused with HTTP {(int)response.StatusCode}.");
        }

        RememberCsrfToken(response);
    }

    public async Task<CaptureEntry> GetAsync(string pathAndQuery, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (_csrfToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Csrf-Token", _csrfToken);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        RememberCsrfToken(response);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return new CaptureEntry("http", "GET", pathAndQuery, (int)response.StatusCode,
            response.Content.Headers.ContentType?.MediaType, DateTimeOffset.UtcNow, body);
    }

    private void RememberCsrfToken(HttpResponseMessage response)
    {
        foreach (var name in CsrfHeaders)
        {
            if (response.Headers.TryGetValues(name, out var values) && values.FirstOrDefault() is { Length: > 0 } token)
            {
                _csrfToken = token;
                return;
            }
        }
    }

    public void Dispose() => _http.Dispose();
}

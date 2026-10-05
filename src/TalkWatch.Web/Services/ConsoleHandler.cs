using System.Net;
using TalkWatch.Core.Talk;

namespace TalkWatch.Web.Services;

/// <summary>
/// The console client's own handler: every request goes to the console the current <see cref="ConsoleTarget"/> names,
/// trusting its pinned certificate, through the tunnel when there is one. When the Console page changes any of that, the
/// next request uses the new settings; the app never restarts for them.
/// </summary>
public sealed class ConsoleHandler(ConsoleConnection connection, ConsoleTunnel tunnel) : HttpMessageHandler
{
    /// <summary>The client's base address; requests are sent on to the console the settings name.</summary>
    public static readonly Uri Placeholder = new("https://console.invalid/");

    private readonly Lock _lock = new();
    private Inner? _inner;

    private sealed record Inner(Uri? Url, string Username, string? Pin, Uri? Proxy, CookieContainer Cookies, HttpMessageInvoker Invoker);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await connection.GetAsync(cancellationToken);
        if (target.Url is null)
        {
            throw new HttpRequestException("No console is set: give its address on the Console page, or as Talk__ConsoleUrl.");
        }

        request.RequestUri = Retarget(request.RequestUri, target.Url);
        var invoker = Current(target);
        if (tunnel.ProxyFor(target) is null)
        {
            return await invoker.SendAsync(request, cancellationToken);
        }

        try
        {
            return await invoker.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e) when (tunnel.Status.State != TunnelState.Up)
        {
            // Through the tunnel, a failure says only that the proxy on loopback refused, or couldn't connect onward:
            // "Connection refused (127.0.0.1:38699)" was what the first real try showed. Say what is actually wrong.
            throw new HttpRequestException($"The VPN to the site isn't up yet{(tunnel.Status.Detail is { } why ? ": " + why : ".")}", e);
        }
    }

    /// <summary>The same path and query on the console's address; ws and wss for the live feed's WebSocket.</summary>
    public static Uri Retarget(Uri? requested, Uri console)
    {
        ArgumentNullException.ThrowIfNull(console);
        var uri = requested is null ? console : requested.IsAbsoluteUri ? requested : new Uri(console, requested);
        var secure = console.Scheme == Uri.UriSchemeHttps;
        var scheme = uri.Scheme is "ws" or "wss" ? (secure ? "wss" : "ws") : console.Scheme;
        return new UriBuilder(uri) { Scheme = scheme, Host = console.Host, Port = console.Port }.Uri;
    }

    private HttpMessageInvoker Current(ConsoleTarget target)
    {
        var proxy = tunnel.ProxyFor(target);
        lock (_lock)
        {
            if (_inner is { } inner && inner.Url == target.Url && inner.Username == target.Username && inner.Pin == target.CertificateSha256 && inner.Proxy == proxy)
            {
                return inner.Invoker;
            }

            // A new console or account starts a new session; a new way to the same console keeps the one it has.
            var cookies = _inner is { } old && old.Url == target.Url && old.Username == target.Username ? old.Cookies : new CookieContainer();
            var previous = _inner;
            _inner = new Inner(target.Url, target.Username, target.CertificateSha256, proxy, cookies,
                new HttpMessageInvoker(ConsoleHttp.CreateHandler(target.CertificateSha256, proxy, cookies), disposeHandler: true));

            // Requests already under way on the old handler finish on it; it goes once they have had time to.
            if (previous is not null)
            {
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => previous.Invoker.Dispose(), TaskScheduler.Default);
            }

            return _inner.Invoker;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner?.Invoker.Dispose();
        }

        base.Dispose(disposing);
    }
}

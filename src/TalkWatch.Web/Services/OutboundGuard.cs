using System.Net;
using System.Net.Sockets;

namespace TalkWatch.Web.Services;

/// <summary>
/// Where alerts and browser notifications may be sent. Their addresses are typed in by people, a Manager among them, or
/// given by a browser, and TalkWatch sends from inside the network: without a check, an alert channel could reach
/// TalkWatch's own database, the console, or a cloud's metadata service, and the test button would say which ports
/// answered. Checked as each connection is made, on the address it is made to, so a name that resolves elsewhere later
/// gets no further; and redirects are not followed, so a server outside cannot send the request back in.
/// </summary>
public static class OutboundGuard
{
    /// <summary>
    /// A handler for sending alerts. <paramref name="privateAllowed"/>: whether the LAN's private addresses may be reached,
    /// as a self-hosted ntfy or Home Assistant often is. TalkWatch's own machine and link-local addresses never may.
    /// </summary>
    public static SocketsHttpHandler Handler(bool privateAllowed) => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = (context, cancellationToken) => ConnectAsync(context.DnsEndPoint, privateAllowed, cancellationToken),
    };

    /// <summary>Why this address may not be sent to, or null when it may.</summary>
    public static string? Problem(IPAddress address, bool privateAllowed)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Broadcast))
        {
            return "TalkWatch's own machine";
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            // 169.254/16 holds the cloud metadata services; 0/8 and 224/3 are no one's to send to.
            if (bytes[0] is 0 or >= 224 || (bytes[0] == 169 && bytes[1] == 254))
            {
                return "a link-local or reserved address";
            }

            var isPrivate = bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and < 32) || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 100 && bytes[1] is >= 64 and < 128);
            return isPrivate && !privateAllowed ? "a private address" : null;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
        {
            return "a link-local or reserved address";
        }

        // fc00::/7, unique local: IPv6's private addresses.
        return (bytes[0] & 0xFE) == 0xFC && !privateAllowed ? "a private address" : null;
    }

    private static async ValueTask<Stream> ConnectAsync(DnsEndPoint endPoint, bool privateAllowed, CancellationToken cancellationToken)
    {
        var addresses = IPAddress.TryParse(endPoint.Host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(endPoint.Host, cancellationToken);
        var allowed = addresses.Where(a => Problem(a, privateAllowed) is null).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException(addresses.Length == 0
                ? $"{endPoint.Host} has no address."
                : $"{endPoint.Host} is {Problem(addresses[0], privateAllowed)}, which TalkWatch doesn't send alerts to.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

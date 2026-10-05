using System.Globalization;
using System.Net;
using System.Text;

namespace TalkWatch.Core.Talk;

/// <summary>
/// A WireGuard client's settings, as a UniFi gateway's VPN server gives them out: one [Interface] for TalkWatch's end of
/// the tunnel, one [Peer] for the gateway. Read from the .conf file the gateway offers for download, or made from the
/// same values typed in one by one, and written out for wireproxy, the userspace WireGuard client TalkWatch runs.
/// </summary>
public sealed record WireGuardConfig
{
    public required string PrivateKey { get; init; }

    /// <summary>TalkWatch's addresses inside the tunnel, such as 192.168.3.2/32.</summary>
    public required IReadOnlyList<string> Addresses { get; init; }

    public IReadOnlyList<string> Dns { get; init; } = [];

    public int? Mtu { get; init; }

    public required string PeerPublicKey { get; init; }

    public string? PresharedKey { get; init; }

    /// <summary>The gateway: its public address, or a name such as the site's dynamic DNS name.</summary>
    public required string EndpointHost { get; init; }

    public required int EndpointPort { get; init; }

    /// <summary>What goes through the tunnel; the console's address has to be inside one of these.</summary>
    public required IReadOnlyList<string> AllowedIps { get; init; }

    public int? PersistentKeepalive { get; init; }

    /// <summary>Whether the endpoint is a name to look up, which can change address, rather than an address.</summary>
    public bool EndpointIsName => !IPAddress.TryParse(EndpointHost, out _);

    /// <summary>The endpoint as written in a config: host:port, with an IPv6 address in brackets.</summary>
    public string Endpoint => Join(EndpointHost, EndpointPort);

    /// <summary>Reads a wg-quick style .conf, such as the one a UniFi gateway gives a VPN client. Throws <see cref="FormatException"/> saying what is wrong.</summary>
    public static WireGuardConfig Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string? section = null;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peers = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim();
                if (section.Equals("Peer", StringComparison.OrdinalIgnoreCase) && ++peers > 1)
                {
                    throw new FormatException("The file has more than one [Peer]; TalkWatch needs only the gateway's.");
                }

                continue;
            }

            var equals = line.IndexOf('=', StringComparison.Ordinal);
            if (section is null || equals < 1)
            {
                throw new FormatException($"'{Shorten(line)}' isn't a setting under [Interface] or [Peer].");
            }

            // A key's base64 can end in '=', so only the first '=' separates the name.
            values[$"{section}.{line[..equals].Trim()}"] = line[(equals + 1)..].Trim();
        }

        if (peers == 0)
        {
            throw new FormatException("The file has no [Peer]: download the client's config from the gateway's VPN server page.");
        }

        var (host, port) = SplitEndpoint(Required(values, "Peer.Endpoint", "the gateway's address"));
        return Validated(new WireGuardConfig
        {
            PrivateKey = Required(values, "Interface.PrivateKey", "the client's private key"),
            Addresses = List(Required(values, "Interface.Address", "the client's address in the tunnel")),
            Dns = values.TryGetValue("Interface.DNS", out var dns) ? List(dns) : [],
            Mtu = values.TryGetValue("Interface.MTU", out var mtu) ? Number(mtu, "MTU") : null,
            PeerPublicKey = Required(values, "Peer.PublicKey", "the gateway's public key"),
            PresharedKey = values.TryGetValue("Peer.PresharedKey", out var psk) && psk.Length > 0 ? psk : null,
            EndpointHost = host,
            EndpointPort = port,
            AllowedIps = values.TryGetValue("Peer.AllowedIPs", out var allowed) ? List(allowed) : ["0.0.0.0/0"],
            PersistentKeepalive = values.TryGetValue("Peer.PersistentKeepalive", out var keep) ? Number(keep, "PersistentKeepalive") : null,
        });
    }

    /// <summary>The same settings typed in one by one. Throws <see cref="FormatException"/> saying what is wrong.</summary>
    public static WireGuardConfig FromFields(
        string? privateKey, string? address, string? peerPublicKey, string? endpoint, string? allowedIps, string? presharedKey, string? dns)
    {
        var (host, port) = SplitEndpoint(Text(endpoint) ?? throw new FormatException("Give the gateway's address, such as vpn.example.com:51820."));
        return Validated(new WireGuardConfig
        {
            PrivateKey = Text(privateKey) ?? throw new FormatException("Give the client's private key."),
            Addresses = List(Text(address) ?? throw new FormatException("Give the client's address in the tunnel, such as 192.168.3.2/32.")),
            PeerPublicKey = Text(peerPublicKey) ?? throw new FormatException("Give the gateway's public key."),
            PresharedKey = Text(presharedKey),
            EndpointHost = host,
            EndpointPort = port,
            AllowedIps = Text(allowedIps) is { } allowed ? List(allowed) : ["0.0.0.0/0"],
            Dns = Text(dns) is { } d ? List(d) : [],
            // A gateway behind NAT forgets a quiet client; 25 seconds is what wg-quick suggests for that.
            PersistentKeepalive = 25,
        });
    }

    /// <summary>The settings as a wg-quick .conf, which is also how they are stored.</summary>
    public string ToConf() => Write(Endpoint, extra: null);

    /// <summary>
    /// The settings for wireproxy: the endpoint as the address it was resolved to (wireproxy resolves a name only when it
    /// starts, so TalkWatch resolves it and restarts on a change), a SOCKS5 proxy on loopback for TalkWatch's requests to
    /// the console, and nothing else listening.
    /// </summary>
    public string ToWireproxy(IPAddress endpointAddress, int socksPort)
    {
        ArgumentNullException.ThrowIfNull(endpointAddress);
        return Write(Join(endpointAddress.ToString(), EndpointPort), $"\n[Socks5]\nBindAddress = 127.0.0.1:{socksPort.ToString(CultureInfo.InvariantCulture)}\n");
    }

    /// <summary>Whether <paramref name="address"/> goes through the tunnel at all.</summary>
    public bool Routes(IPAddress address) => AllowedIps.Any(cidr => IPNetwork.TryParse(cidr, out var network) && network.Contains(address));

    private string Write(string endpoint, string? extra)
    {
        var conf = new StringBuilder();
        conf.Append("[Interface]\n");
        conf.Append(CultureInfo.InvariantCulture, $"PrivateKey = {PrivateKey}\n");
        conf.Append(CultureInfo.InvariantCulture, $"Address = {string.Join(", ", Addresses)}\n");
        if (Dns.Count > 0)
        {
            conf.Append(CultureInfo.InvariantCulture, $"DNS = {string.Join(", ", Dns)}\n");
        }

        if (Mtu is { } mtu)
        {
            conf.Append(CultureInfo.InvariantCulture, $"MTU = {mtu}\n");
        }

        conf.Append("\n[Peer]\n");
        conf.Append(CultureInfo.InvariantCulture, $"PublicKey = {PeerPublicKey}\n");
        if (PresharedKey is { } psk)
        {
            conf.Append(CultureInfo.InvariantCulture, $"PresharedKey = {psk}\n");
        }

        conf.Append(CultureInfo.InvariantCulture, $"AllowedIPs = {string.Join(", ", AllowedIps)}\n");
        conf.Append(CultureInfo.InvariantCulture, $"Endpoint = {endpoint}\n");
        if (PersistentKeepalive is { } keep)
        {
            conf.Append(CultureInfo.InvariantCulture, $"PersistentKeepalive = {keep}\n");
        }

        return conf.Append(extra).ToString();
    }

    private static WireGuardConfig Validated(WireGuardConfig config)
    {
        Key(config.PrivateKey, "The client's private key");
        Key(config.PeerPublicKey, "The gateway's public key");
        if (config.PresharedKey is { } psk)
        {
            Key(psk, "The preshared key");
        }

        foreach (var cidr in config.Addresses.Concat(config.AllowedIps))
        {
            if (!IPNetwork.TryParse(cidr, out _) && !IPAddress.TryParse(cidr, out _))
            {
                throw new FormatException($"'{Shorten(cidr)}' isn't an address or network, such as 192.168.3.2/32.");
            }
        }

        foreach (var server in config.Dns)
        {
            if (!IPAddress.TryParse(server, out _))
            {
                throw new FormatException($"The DNS server '{Shorten(server)}' isn't an address.");
            }
        }

        return config;
    }

    // A WireGuard key is 32 bytes in base64: 44 characters ending in '='.
    private static void Key(string value, string what)
    {
        var bytes = new byte[32];
        if (value.Length != 44 || !Convert.TryFromBase64String(value, bytes, out var written) || written != 32)
        {
            throw new FormatException($"{what} isn't a WireGuard key: 44 characters of base64, ending in '='.");
        }
    }

    private static (string Host, int Port) SplitEndpoint(string endpoint)
    {
        string host, port;
        if (endpoint.StartsWith('['))
        {
            var close = endpoint.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || close + 1 >= endpoint.Length || endpoint[close + 1] != ':')
            {
                throw new FormatException($"'{Shorten(endpoint)}' isn't an address and port, such as [2001:db8::1]:51820.");
            }

            (host, port) = (endpoint[1..close], endpoint[(close + 2)..]);
        }
        else
        {
            var colon = endpoint.LastIndexOf(':');
            if (colon < 1 || endpoint.IndexOf(':', StringComparison.Ordinal) != colon)
            {
                throw new FormatException($"'{Shorten(endpoint)}' isn't an address and port, such as vpn.example.com:51820.");
            }

            (host, port) = (endpoint[..colon], endpoint[(colon + 1)..]);
        }

        if (!int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
        {
            throw new FormatException($"The gateway's port '{Shorten(port)}' isn't a number from 1 to 65535.");
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            throw new FormatException($"'{Shorten(host)}' isn't an address or host name.");
        }

        return (host, number);
    }

    private static string Join(string host, int port) =>
        (IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host)
        + ":" + port.ToString(CultureInfo.InvariantCulture);

    private static string Required(Dictionary<string, string> values, string key, string what) =>
        values.TryGetValue(key, out var value) && value.Length > 0
            ? value
            : throw new FormatException($"The file doesn't give {what} ({key.Replace(".", " ", StringComparison.Ordinal)}).");

    private static int Number(string value, string what) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : throw new FormatException($"{what} '{Shorten(value)}' isn't a number.");

    private static List<string> List(string value) =>
        [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Errors are shown on the page, and a malformed line can hold a private key: no more of it than a key's first few
    // characters is ever repeated back.
    private static string Shorten(string value) => value.Length <= 16 ? value : value[..12] + "...";
}

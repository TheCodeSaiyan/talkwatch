using System.Net;
using TalkWatch.Core.Talk;

namespace TalkWatch.Core.Tests;

public class WireGuardConfigTests
{
    // Keys made up for the tests: 32 bytes each, in base64 as WireGuard writes them.
    private static readonly string ClientKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string GatewayKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());
    private static readonly string SharedKey = Convert.ToBase64String(Enumerable.Repeat((byte)3, 32).ToArray());

    // The shape of the file a UniFi gateway's WireGuard VPN server offers for a client.
    private static string UniFiFile(string endpoint = "talk-site.example.net:51820") => $"""
        [Interface]
        PrivateKey = {ClientKey}
        Address = 192.168.3.2/32
        DNS = 192.168.3.1

        [Peer]
        PublicKey = {GatewayKey}
        PresharedKey = {SharedKey}
        AllowedIPs = 0.0.0.0/0
        Endpoint = {endpoint}
        """;

    [Fact]
    public void The_file_a_UniFi_gateway_gives_a_client_is_read_whole()
    {
        var config = WireGuardConfig.Parse(UniFiFile());

        Assert.Equal(ClientKey, config.PrivateKey);
        Assert.Equal(["192.168.3.2/32"], config.Addresses);
        Assert.Equal(["192.168.3.1"], config.Dns);
        Assert.Equal(GatewayKey, config.PeerPublicKey);
        Assert.Equal(SharedKey, config.PresharedKey);
        Assert.Equal("talk-site.example.net", config.EndpointHost);
        Assert.Equal(51820, config.EndpointPort);
        Assert.True(config.EndpointIsName);
        Assert.True(config.Routes(IPAddress.Parse("192.168.1.1")));
    }

    [Fact]
    public void Typed_in_fields_make_the_same_settings_as_the_file()
    {
        var typed = WireGuardConfig.FromFields(ClientKey, "192.168.3.2/32", GatewayKey, "talk-site.example.net:51820", "0.0.0.0/0", SharedKey, "192.168.3.1");
        var read = WireGuardConfig.Parse(UniFiFile());

        // Typed in, the client also keeps the tunnel open through NAT, which the file leaves to the gateway.
        Assert.Equal(25, typed.PersistentKeepalive);
        Assert.Equal(read.ToConf(), (typed with { PersistentKeepalive = null }).ToConf());
    }

    [Fact]
    public void Written_back_out_it_reads_the_same()
    {
        var config = WireGuardConfig.Parse(UniFiFile());

        Assert.Equal(config.ToConf(), WireGuardConfig.Parse(config.ToConf()).ToConf());
    }

    // wireproxy looks a name up only when it starts, so TalkWatch hands it the address it found, and restarts it when
    // the site's dynamic DNS name moves.
    [Fact]
    public void Wireproxy_gets_the_looked_up_address_and_a_SOCKS5_proxy_on_loopback_only()
    {
        var wireproxy = WireGuardConfig.Parse(UniFiFile()).ToWireproxy(IPAddress.Parse("203.0.113.7"), 41080);

        Assert.Contains("Endpoint = 203.0.113.7:51820", wireproxy, StringComparison.Ordinal);
        Assert.Contains("[Socks5]\nBindAddress = 127.0.0.1:41080", wireproxy, StringComparison.Ordinal);
        Assert.DoesNotContain("talk-site.example.net", wireproxy, StringComparison.Ordinal);
    }

    [Fact]
    public void An_IPv6_gateway_is_written_in_brackets()
    {
        var config = WireGuardConfig.Parse(UniFiFile("[2001:db8::7]:51820"));

        Assert.False(config.EndpointIsName);
        Assert.Equal("[2001:db8::7]:51820", config.Endpoint);
    }

    [Theory]
    [InlineData("[Interface]\nAddress = 192.168.3.2/32\n[Peer]\nEndpoint = a.example:51820", "private key")]
    [InlineData("[Interface]\nPrivateKey = KEY\nAddress = 192.168.3.2/32", "no [Peer]")]
    [InlineData("[Interface]\nPrivateKey = KEY\nAddress = 192.168.3.2/32\n[Peer]\nPublicKey = PEER\nEndpoint = a.example", "address and port")]
    [InlineData("[Interface]\nPrivateKey = KEY\nAddress = 192.168.3.2/32\n[Peer]\nPublicKey = PEER\nEndpoint = a.example:99999", "1 to 65535")]
    [InlineData("[Interface]\nPrivateKey = notakey\nAddress = 192.168.3.2/32\n[Peer]\nPublicKey = PEER\nEndpoint = a.example:51820", "isn't a WireGuard key")]
    [InlineData("PrivateKey = KEY", "isn't a setting")]
    public void A_file_that_will_not_work_says_why(string file, string why)
    {
        var e = Assert.Throws<FormatException>(() => WireGuardConfig.Parse(file.Replace("KEY", ClientKey, StringComparison.Ordinal).Replace("PEER", GatewayKey, StringComparison.Ordinal)));

        Assert.Contains(why, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complaint_never_repeats_a_private_key_back()
    {
        var e = Assert.Throws<FormatException>(() => WireGuardConfig.Parse($"PrivateKey {ClientKey}"));

        Assert.DoesNotContain(ClientKey[..20], e.Message, StringComparison.Ordinal);
    }
}

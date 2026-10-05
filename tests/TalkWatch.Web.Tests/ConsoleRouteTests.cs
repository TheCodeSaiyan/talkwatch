using Microsoft.AspNetCore.DataProtection;
using TalkWatch.Data;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>The way to the console, short of running a tunnel: which settings win, and what the helpers are told.</summary>
public sealed class ConsoleRouteTests
{
    private static readonly ChannelSecrets Secrets = new(new EphemeralDataProtectionProvider());
    private static readonly string ClientKey = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string GatewayKey = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    private static string Conf(string allowed = "0.0.0.0/0") =>
        $"[Interface]\nPrivateKey = {ClientKey}\nAddress = 192.168.3.2/32\n[Peer]\nPublicKey = {GatewayKey}\nAllowedIPs = {allowed}\nEndpoint = site.example.net:51820\n";

    [Fact]
    public void With_nothing_saved_the_settings_decide()
    {
        var target = ConsoleConnection.Combine(null, new TalkOptions { ConsoleUrl = new Uri("https://192.168.1.1"), Username = "u", Password = "p" }, Secrets);

        Assert.Equal((new Uri("https://192.168.1.1"), "u", "p", ConsoleRoute.Direct, false), (target.Url, target.Username, target.Password, target.Route, target.FromPage));
        Assert.Null(target.Problem);
    }

    [Fact]
    public void A_field_saved_on_the_page_wins_and_one_left_empty_falls_back()
    {
        var saved = new ConsoleSettings { Username = "from-page", ProtectedPassword = Secrets.Protect("page password"), Route = ConsoleRoute.WireGuard, ProtectedWireGuardConfig = Secrets.Protect(Conf()) };
        var target = ConsoleConnection.Combine(saved, new TalkOptions { ConsoleUrl = new Uri("https://192.168.1.1"), Username = "from-env", Password = "env password" }, Secrets);

        Assert.Equal(("from-page", "page password", new Uri("https://192.168.1.1")), (target.Username, target.Password, target.Url));
        Assert.Equal("site.example.net", target.WireGuard!.EndpointHost);
        Assert.Null(target.Problem);
    }

    [Fact]
    public void WireGuard_from_the_environment_alone_works_too()
    {
        var target = ConsoleConnection.Combine(null, new TalkOptions { ConsoleUrl = new Uri("https://192.168.1.1"), Route = ConsoleRoute.WireGuard, WireGuardConfig = Conf() }, Secrets);

        Assert.NotNull(target.WireGuard);
        Assert.Null(target.Problem);
    }

    [Theory]
    [InlineData(ConsoleRoute.WireGuard, null, null, "no WireGuard settings")]
    [InlineData(ConsoleRoute.Tailscale, null, null, "no auth key")]
    [InlineData(ConsoleRoute.Tailscale, "tskey-client-abc", null, "needs at least one tag")]
    public void A_route_without_what_it_needs_says_so(ConsoleRoute route, string? key, string? tags, string problem)
    {
        var target = ConsoleConnection.Combine(null, new TalkOptions { Route = route, TailscaleAuthKey = key, TailscaleTags = tags }, Secrets);

        Assert.Contains(problem, target.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void A_console_the_tunnel_does_not_carry_is_flagged()
    {
        var target = ConsoleConnection.Combine(null, new TalkOptions { ConsoleUrl = new Uri("https://10.0.0.1"), Route = ConsoleRoute.WireGuard, WireGuardConfig = Conf("192.168.1.0/24") }, Secrets);

        Assert.Contains("isn't in the tunnel's allowed addresses", target.Problem, StringComparison.Ordinal);
    }

    // The console keeps its own LAN address whichever way TalkWatch reaches it: the certificate pin and the host name are
    // what they would be on the site.
    [Theory]
    [InlineData("https://console.invalid/proxy/talk/api/info?x=1", "https://192.168.1.1/proxy/talk/api/info?x=1")]
    [InlineData("wss://console.invalid/proxy/talk/", "wss://192.168.1.1/proxy/talk/")]
    [InlineData("/api/auth/login", "https://192.168.1.1/api/auth/login")]
    public void Every_request_goes_to_the_console_the_settings_name(string requested, string sent) =>
        Assert.Equal(new Uri(sent), ConsoleHandler.Retarget(new Uri(requested, UriKind.RelativeOrAbsolute), new Uri("https://192.168.1.1")));

    [Fact]
    public void Tailscale_reads_its_key_from_a_file_and_takes_the_sites_routes()
    {
        var args = ConsoleTunnel.TailscaleUp("/tmp/t/tailscaled.sock", "/tmp/t/auth-key", "tag:talkwatch, tag:site");

        Assert.Contains("--auth-key=file:/tmp/t/auth-key", args);
        Assert.Contains("--accept-routes", args);
        Assert.Contains("--advertise-tags=tag:talkwatch,tag:site", args);
        Assert.DoesNotContain(args, a => a.Contains("tskey", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("tskey-client-abc-123", "tskey-client-abc-123?ephemeral=true&preauthorized=true")]
    [InlineData("tskey-client-abc-123?ephemeral=false", "tskey-client-abc-123?ephemeral=false")]
    [InlineData("tskey-auth-abc-123", "tskey-auth-abc-123")]
    public void An_OAuth_client_secret_registers_an_ephemeral_approved_node(string given, string used) =>
        Assert.Equal(used, ConsoleTunnel.TailscaleAuthKey(given));

    // A gateway's dynamic DNS name kept in Cloudflare with the proxy on answers with Cloudflare's address, which never
    // passes WireGuard on: the first real gateway tried was set up that way.
    [Theory]
    [InlineData("104.21.42.106", true)]
    [InlineData("172.67.161.62", true)]
    [InlineData("2606:4700::6810:2a6a", true)]
    [InlineData("92.237.165.255", false)]
    [InlineData("203.0.113.7", false)]
    public void A_gateway_name_pointing_at_Cloudflares_proxy_is_recognised(string address, bool cloudflare) =>
        Assert.Equal(cloudflare, ConsoleTunnel.IsCloudflareProxy(System.Net.IPAddress.Parse(address)));

    [Fact]
    public void The_latest_handshake_is_read_from_WireGuards_own_figures()
    {
        const string metrics = "private_key=00\npublic_key=11\nlast_handshake_time_sec=1700000000\nlast_handshake_time_nsec=5\nlast_handshake_time_sec=1700000300\n";

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000300), ConsoleTunnel.LastHandshake(metrics));
        Assert.Null(ConsoleTunnel.LastHandshake("last_handshake_time_sec=0\n"));
    }
}

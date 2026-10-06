using System.Net;
using System.Net.Sockets;
using TalkWatch.Web.Services;

namespace TalkWatch.Web.Tests;

/// <summary>Alerts and browser notifications go where they're meant to, never into TalkWatch's own machine or network.</summary>
public sealed class OutboundGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("169.254.169.254")]
    [InlineData("fe80::1")]
    [InlineData("224.0.0.1")]
    public void TalkWatchs_own_machine_and_link_local_addresses_are_never_sent_to(string address)
    {
        Assert.NotNull(OutboundGuard.Problem(IPAddress.Parse(address), privateAllowed: true));
        Assert.NotNull(OutboundGuard.Problem(IPAddress.Parse(address), privateAllowed: false));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("100.100.1.1")]
    [InlineData("fd12:3456::1")]
    [InlineData("::ffff:192.168.1.1")]
    public void The_lans_private_addresses_only_when_allowed(string address)
    {
        Assert.Null(OutboundGuard.Problem(IPAddress.Parse(address), privateAllowed: true));
        Assert.Equal("a private address", OutboundGuard.Problem(IPAddress.Parse(address), privateAllowed: false));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:4860:4860::8888")]
    public void Public_addresses_always(string address) =>
        Assert.Null(OutboundGuard.Problem(IPAddress.Parse(address), privateAllowed: false));

    // The address is checked as the connection is made, so nothing reaches a service on TalkWatch's own machine.
    [Fact]
    public async Task A_request_to_TalkWatchs_own_machine_is_refused_before_it_connects()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new HttpClient(OutboundGuard.Handler(privateAllowed: true));
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.PostAsync(new Uri($"http://localhost:{port}/hook"), null, Ct));

            Assert.Contains("own machine", refused.Message, StringComparison.Ordinal);
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public void Redirects_are_not_followed() => Assert.False(OutboundGuard.Handler(privateAllowed: true).AllowAutoRedirect);
}

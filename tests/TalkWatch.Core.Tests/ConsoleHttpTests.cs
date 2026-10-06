using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TalkWatch.Core.Talk;

namespace TalkWatch.Core.Tests;

/// <summary>The console is trusted by its pinned certificate alone, or, with no pin, by a certificate that validates.</summary>
public sealed class ConsoleHttpTests
{
    private static X509Certificate2 SelfSigned(string name)
    {
        using var key = ECDsa.Create();
        return new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static string Pin(X509Certificate2 certificate) =>
        string.Join(":", Convert.ToHexString(SHA256.HashData(certificate.RawData)).Chunk(2).Select(c => new string(c))).ToLowerInvariant();

    [Fact]
    public void With_a_pin_only_the_pinned_certificate_is_trusted_even_against_one_that_validates()
    {
        using var console = SelfSigned("console");
        using var other = SelfSigned("console");

        Assert.True(ConsoleHttp.Trusts(console, SslPolicyErrors.RemoteCertificateChainErrors, Pin(console)));
        // Another certificate for the same name, as a public authority would issue to anyone who controls it.
        Assert.False(ConsoleHttp.Trusts(other, SslPolicyErrors.None, Pin(console)));
        Assert.False(ConsoleHttp.Trusts(null, SslPolicyErrors.None, Pin(console)));
    }

    [Fact]
    public void Without_a_pin_only_a_certificate_that_validates()
    {
        using var console = SelfSigned("console");

        Assert.True(ConsoleHttp.Trusts(console, SslPolicyErrors.None, null));
        Assert.False(ConsoleHttp.Trusts(console, SslPolicyErrors.RemoteCertificateChainErrors, " "));
    }
}

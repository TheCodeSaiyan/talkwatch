using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace TalkWatch.Web.Tests;

/// <summary>
/// An OpenID Connect provider just big enough for TalkWatch's handler to sign someone in against: discovery, signing
/// keys, a token endpoint that issues a signed ID token, and user info with groups. The browser's visit to the
/// authorise page is <see cref="Authorise"/>: it answers as the provider would, with a redirect back carrying a code.
/// </summary>
public sealed class FakeOidcProvider : HttpMessageHandler
{
    public const string Authority = "https://idp.test";
    public const string ClientId = "talkwatch";
    public const string ClientSecret = "fake-client-secret";

    /// <summary>The email the provider gives for whoever signs in next; none when null.</summary>
    public string? Email { get; set; }

    /// <summary>Whether the provider says it checked that email: true, false, or not said when null.</summary>
    public bool? EmailVerified { get; set; }

    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "fake-key" };
    private readonly Dictionary<string, (string Nonce, string Subject, string Username, string[] Groups)> _codes = new(StringComparer.Ordinal);

    /// <summary>What the provider's authorise page does for a signed-in person: a redirect back to the app with a code.</summary>
    public Uri Authorise(Uri authoriseRequest, string subject, string username, params string[] groups)
    {
        var query = HttpUtility.ParseQueryString(authoriseRequest.Query);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        _codes[code] = (query["nonce"]!, subject, username, groups);
        return new Uri($"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(query["state"]!)}");
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        switch (request.RequestUri!.AbsolutePath)
        {
            case "/.well-known/openid-configuration":
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        {"issuer": "{{Authority}}", "authorization_endpoint": "{{Authority}}/authorize", "token_endpoint": "{{Authority}}/token",
                         "userinfo_endpoint": "{{Authority}}/userinfo", "jwks_uri": "{{Authority}}/jwks", "response_types_supported": ["code"],
                         "subject_types_supported": ["public"], "id_token_signing_alg_values_supported": ["RS256"]}
                        """, Encoding.UTF8, "application/json"),
                };

            case "/jwks":
                var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
                return Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });

            case "/token":
                var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
                if (form["code"] is not { } code || !_codes.Remove(code, out var grant) || form["code_verifier"] is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("""{"error":"invalid_grant"}""", Encoding.UTF8, "application/json") };
                }

                _lastSubject = grant;
                var now = DateTimeOffset.UtcNow;
                var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = Authority,
                    Audience = ClientId,
                    IssuedAt = now.UtcDateTime,
                    NotBefore = now.UtcDateTime,
                    Expires = now.AddMinutes(5).UtcDateTime,
                    Claims = new Dictionary<string, object> { ["sub"] = grant.Subject, ["nonce"] = grant.Nonce },
                    SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
                });
                return Json(new { access_token = "fake-access-token", token_type = "Bearer", expires_in = 300, id_token = idToken });

            case "/userinfo":
                return Json(new { sub = _lastSubject.Subject, preferred_username = _lastSubject.Username, groups = _lastSubject.Groups, email = Email, email_verified = EmailVerified });

            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private (string Nonce, string Subject, string Username, string[] Groups) _lastSubject;

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Rsa?.Dispose();
        }

        base.Dispose(disposing);
    }
}

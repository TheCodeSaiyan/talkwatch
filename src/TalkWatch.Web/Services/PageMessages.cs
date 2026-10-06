using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace TalkWatch.Web.Services;

/// <summary>
/// The note a page shows after a form is sent ("Saved.", "Choose a line."), carried in the address as ?msg=. Anyone can
/// write an address, so a link could put any words in that note, inside TalkWatch's own page: "Your session expired, sign
/// in at ...". So TalkWatch signs the notes it sends, for an hour, and a page shows only a signed one; the words stay
/// readable in the address.
/// </summary>
public static class PageMessages
{
    private const string Purpose = "TalkWatch.PageMessages.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>The signature for a note, for an address made in a page rather than a redirect.</summary>
    public static string Sign(IDataProtectionProvider protection, string message) =>
        Protector(protection).Protect(Digest(message), Lifetime);

    /// <summary>An address with a note on it, signed.</summary>
    public static string WithMessage(IDataProtectionProvider protection, string path, string message) =>
        QueryHelpers.AddQueryString(path, new Dictionary<string, string?> { ["msg"] = message, ["sig"] = Sign(protection, message) });

    public static void UsePageMessages(this WebApplication app)
    {
        var protection = app.Services.GetRequiredService<IDataProtectionProvider>();
        app.Use(async (context, next) =>
        {
            // A note that TalkWatch didn't sign is left out before any page reads it.
            var query = context.Request.Query;
            if (query.TryGetValue("msg", out var message) && !Valid(protection, message, query["sig"]))
            {
                var kept = query.Where(q => q.Key is not ("msg" or "sig")).ToDictionary(q => q.Key, q => q.Value);
                context.Request.QueryString = QueryString.Create(kept);
            }

            // Every redirect to a page here that carries a note: signed on its way out.
            context.Response.OnStarting(() =>
            {
                var location = context.Response.Headers.Location.ToString();
                if (context.Response.StatusCode is >= 300 and < 400 && location.StartsWith('/') && !location.StartsWith("//", StringComparison.Ordinal)
                    && location.Contains("msg=", StringComparison.Ordinal) && !location.Contains("sig=", StringComparison.Ordinal))
                {
                    var question = location.IndexOf('?', StringComparison.Ordinal);
                    if (question >= 0 && QueryHelpers.ParseQuery(location[question..]).TryGetValue("msg", out var note) && note.Count == 1)
                    {
                        context.Response.Headers.Location = QueryHelpers.AddQueryString(location, "sig", Sign(protection, note[0]!));
                    }
                }

                return Task.CompletedTask;
            });

            await next();
        });
    }

    private static bool Valid(IDataProtectionProvider protection, StringValues message, StringValues signature)
    {
        if (message.Count != 1 || signature.Count != 1)
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Protector(protection).Unprotect(signature[0]!)), Encoding.ASCII.GetBytes(Digest(message[0]!)));
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();

    private static string Digest(string message) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(message)));
}

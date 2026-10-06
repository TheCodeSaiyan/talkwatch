using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TalkWatch.Data;

namespace TalkWatch.Web.Services;

/// <summary>
/// The key that encrypts the data-protection keys, which in turn encrypt every credential TalkWatch keeps: the console's
/// password, the mail password, bot and channel tokens, tunnel keys, and sign-in cookies. The data-protection keys live in
/// the database, so without this a copy of the database alone was enough to read every one of them. This key never goes
/// into the database: it is DataProtection__Key, given as a secret, or else a key TalkWatch makes on first start and
/// keeps on the data volume (DataProtection__KeyFile, by default .keys/data-protection.key in the audio folder).
/// </summary>
public sealed class KeyEncryptionKey
{
    private const int Size = 32;

    private KeyEncryptionKey(byte[] key, string source) => (Key, Source) = (key, source);

    internal byte[] Key { get; }

    /// <summary>Where it came from, for the log: never the key itself.</summary>
    public string Source { get; }

    /// <summary>
    /// The secret if one is given, else the key file, made now if there is none. Throws, stopping start-up, when neither
    /// can be had: going on would keep the credentials unencrypted.
    /// </summary>
    public static KeyEncryptionKey Load(IConfiguration configuration, string audioPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var options = configuration.GetSection(KeyRingOptions.Section).Get<KeyRingOptions>() ?? new KeyRingOptions();
        if (options.Key is { Length: > 0 } secret)
        {
            if (secret.Trim().Length < 16)
            {
                throw new InvalidOperationException("DataProtection__Key is too short to be a key: give at least 16 characters, such as the output of openssl rand -base64 32.");
            }

            // Any secret of enough length, stretched to an AES key; the label keeps it from being used as anything else's key.
            return new KeyEncryptionKey(HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret.Trim()), Size, info: "TalkWatch data-protection keys v1"u8.ToArray()),
                "DataProtection__Key");
        }

        var path = options.KeyFile is { Length: > 0 } file ? file : Path.Combine(audioPath, ".keys", "data-protection.key");
        try
        {
            if (File.Exists(path))
            {
                var key = Convert.FromBase64String(File.ReadAllText(path).Trim());
                return key.Length == Size ? new KeyEncryptionKey(key, path) : throw new InvalidOperationException($"{path} does not hold a {Size}-byte key in base64.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var made = RandomNumberGenerator.GetBytes(Size);
            // Readable by TalkWatch's own user only, where the file system has such a thing.
            var create = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                create.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var writer = new StreamWriter(path, Encoding.ASCII, create))
            {
                writer.Write(Convert.ToBase64String(made));
            }

            return new KeyEncryptionKey(made, path + " (made now)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            throw new InvalidOperationException(
                $"TalkWatch keeps its credentials encrypted with a key outside the database, and could not read or make {path}: {e.Message} " +
                "Give one as the DataProtection__Key secret, or set DataProtection__KeyFile to a file on a volume TalkWatch can write.", e);
        }
    }

    /// <summary>Encrypts and decrypts with AES-GCM: a fresh nonce each time, and the tag checked on the way back.</summary>
    internal byte[] Seal(byte[] plain)
    {
        var sealedBytes = new byte[12 + 16 + plain.Length];
        var nonce = sealedBytes.AsSpan(0, 12);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(Key, 16);
        aes.Encrypt(nonce, plain, sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16));
        return sealedBytes;
    }

    internal byte[] Open(byte[] sealedBytes)
    {
        var plain = new byte[sealedBytes.Length - 28];
        using var aes = new AesGcm(Key, 16);
        try
        {
            aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain);
        }
        catch (AuthenticationTagMismatchException e)
        {
            throw new CryptographicException(
                "The data-protection keys were encrypted with a different key: DataProtection__Key or its key file has changed since. Put the old one back to read them.", e);
        }

        return plain;
    }
}

/// <summary>Encrypts each data-protection key's secret part as it is written to the database.</summary>
public sealed class KeyRingEncryptor(KeyEncryptionKey key) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        ArgumentNullException.ThrowIfNull(plaintextElement);
        var sealedBytes = key.Seal(Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting)));
        return new EncryptedXmlInfo(
            new XElement("encryptedKey",
                new XComment(" Encrypted with TalkWatch's key-encryption key: DataProtection__Key, or its key file. "),
                new XElement("value", Convert.ToBase64String(sealedBytes))),
            typeof(KeyRingDecryptor));
    }
}

/// <summary>Made by data protection by its type name, as the stored key names it, so it finds the key through the services.</summary>
public sealed class KeyRingDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        ArgumentNullException.ThrowIfNull(encryptedElement);
        var key = services.GetRequiredService<KeyEncryptionKey>();
        var sealedBytes = Convert.FromBase64String((string?)encryptedElement.Element("value") ?? "");
        return XElement.Parse(Encoding.UTF8.GetString(key.Open(sealedBytes)));
    }
}

public static class KeyRing
{
    private static readonly XNamespace DataProtection = "http://schemas.asp.net/2015/03/dataProtection";

    /// <summary>Data protection with its keys kept in the database, encrypted with the key-encryption key.</summary>
    public static void AddKeyRing(this WebApplicationBuilder builder)
    {
        var key = KeyEncryptionKey.Load(builder.Configuration, builder.Configuration.GetSection(AudioOptions.Section).Get<AudioOptions>()?.Path ?? new AudioOptions().Path);
        builder.Services.AddSingleton(key);
        builder.Services.AddDataProtection()
            .SetApplicationName("TalkWatch")
            .PersistKeysToDbContext<TalkWatchDbContext>();
        builder.Services.Configure<KeyManagementOptions>(o => o.XmlEncryptor = new KeyRingEncryptor(key));
    }

    /// <summary>
    /// Encrypts what was stored before keys were: each data-protection key's secret, as data protection itself would have
    /// on writing it, and each two-factor key and recovery code. Runs at start-up, after the schema is up to date.
    /// </summary>
    public static async Task EncryptStoredAsync(IServiceProvider services, TalkWatchDbContext db, CancellationToken cancellationToken)
    {
        var encryptor = new KeyRingEncryptor(services.GetRequiredService<KeyEncryptionKey>());
        foreach (var row in await db.DataProtectionKeys.Where(k => k.Xml != null && !k.Xml.Contains("encryptedSecret")).ToListAsync(cancellationToken))
        {
            var xml = XElement.Parse(row.Xml!);
            foreach (var secret in xml.Descendants().Where(e => (bool?)e.Attribute(DataProtection + "requiresEncryption") == true).ToList())
            {
                var encrypted = encryptor.Encrypt(secret);
                secret.ReplaceWith(new XElement(DataProtection + "encryptedSecret",
                    new XAttribute("decryptorType", encrypted.DecryptorType.AssemblyQualifiedName!), encrypted.EncryptedElement));
            }

            row.Xml = xml.ToString(SaveOptions.DisableFormatting);
        }

        var protector = ProtectedTokenUserStore.Protector(services.GetRequiredService<IDataProtectionProvider>());
        foreach (var token in await db.UserTokens.Where(t => t.Value != null && !t.Value.StartsWith(ProtectedTokenUserStore.Marker)).ToListAsync(cancellationToken))
        {
            token.Value = ProtectedTokenUserStore.Marker + protector.Protect(token.Value!);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>
/// Identity's store, with the tokens it keeps for people (the two-factor key, recovery codes) encrypted. One stored
/// before this is read as it is, and encrypted at the next start.
/// </summary>
public sealed class ProtectedTokenUserStore(TalkWatchDbContext context, IDataProtectionProvider protection, IdentityErrorDescriber? describer = null)
    : UserStore<AppUser, IdentityRole<Guid>, TalkWatchDbContext, Guid>(context, describer)
{
    internal const string Marker = "dp1:";
    private readonly IDataProtector _protector = Protector(protection);

    internal static IDataProtector Protector(IDataProtectionProvider protection) => protection.CreateProtector("TalkWatch.UserTokens.v1");

    public override Task SetTokenAsync(AppUser user, string loginProvider, string name, string? value, CancellationToken cancellationToken) =>
        base.SetTokenAsync(user, loginProvider, name, value is null ? null : Marker + _protector.Protect(value), cancellationToken);

    public override async Task<string?> GetTokenAsync(AppUser user, string loginProvider, string name, CancellationToken cancellationToken)
    {
        var value = await base.GetTokenAsync(user, loginProvider, name, cancellationToken);
        return value is not null && value.StartsWith(Marker, StringComparison.Ordinal) ? _protector.Unprotect(value[Marker.Length..]) : value;
    }
}

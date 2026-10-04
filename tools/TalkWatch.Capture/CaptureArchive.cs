using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TalkWatch.Capture;

public sealed record CaptureManifest(
    DateTimeOffset CapturedUtc,
    string Source,
    string? UnifiOsVersion,
    string? TalkVersion,
    string? Note);

/// <summary>One recorded exchange. Kind is "http" or "ws" (a WebSocket message, where Method is its direction).</summary>
public sealed record CaptureEntry(
    string Kind,
    string Method,
    string Path,
    int Status,
    string? ContentType,
    DateTimeOffset At,
    byte[] Body);

/// <summary>
/// A capture held only in memory and on disk encrypted. The plaintext never touches the file system:
/// it is zipped in memory and sealed with AES-GCM, so a copied file is useless without the key.
/// </summary>
public sealed class CaptureArchive
{
    // Written in the clear at the start of the file, and bound into the tag as associated data.
    private static readonly byte[] Magic = "TWCAP1\n"u8.ToArray();
    private const int NonceLength = 12;
    private const int TagLength = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public CaptureArchive(CaptureManifest manifest, IReadOnlyList<CaptureEntry> entries)
    {
        Manifest = manifest;
        Entries = entries;
    }

    public CaptureManifest Manifest { get; }

    public IReadOnlyList<CaptureEntry> Entries { get; }

    public void Save(string path, CaptureKey key) => File.WriteAllBytes(path, Seal(Pack(), key));

    public static CaptureArchive Load(string path, CaptureKey key) => Unpack(Open(File.ReadAllBytes(path), key));

    internal static byte[] Seal(byte[] plaintext, CaptureKey key)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var tag = new byte[TagLength];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(key.EncryptionKey, TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Magic);
        }

        return [.. Magic, .. nonce, .. tag, .. ciphertext];
    }

    internal static byte[] Open(byte[] sealedBytes, CaptureKey key)
    {
        if (sealedBytes.Length < Magic.Length + NonceLength + TagLength || !sealedBytes.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Not a TalkWatch capture archive.");
        }

        var nonce = sealedBytes.AsSpan(Magic.Length, NonceLength);
        var tag = sealedBytes.AsSpan(Magic.Length + NonceLength, TagLength);
        var ciphertext = sealedBytes.AsSpan(Magic.Length + NonceLength + TagLength);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key.EncryptionKey, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Magic);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException("The archive could not be decrypted: wrong key, or the file has been altered.");
        }

        return plaintext;
    }

    private byte[] Pack()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.json", JsonSerializer.SerializeToUtf8Bytes(Manifest, Json));
            var index = Entries.Select((e, i) => new EntryIndex(e.Kind, e.Method, e.Path, e.Status, e.ContentType, e.At, $"bodies/{i:D5}")).ToList();
            Write(zip, "entries.json", JsonSerializer.SerializeToUtf8Bytes(index, Json));
            for (var i = 0; i < Entries.Count; i++)
            {
                Write(zip, index[i].BodyName, Entries[i].Body);
            }
        }

        return buffer.ToArray();
    }

    private static CaptureArchive Unpack(byte[] zipBytes)
    {
        using var zip = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var manifest = JsonSerializer.Deserialize<CaptureManifest>(Read(zip, "manifest.json"), Json)
            ?? throw new InvalidDataException("The archive has no manifest.");
        var index = JsonSerializer.Deserialize<List<EntryIndex>>(Read(zip, "entries.json"), Json) ?? [];
        var entries = index.Select(e => new CaptureEntry(e.Kind, e.Method, e.Path, e.Status, e.ContentType, e.At, Read(zip, e.BodyName))).ToList();
        return new CaptureArchive(manifest, entries);
    }

    private static void Write(ZipArchive zip, string name, byte[] content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        stream.Write(content);
    }

    private static byte[] Read(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name) ?? throw new InvalidDataException($"The archive is missing {name}.");
        using var stream = entry.Open();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    internal static string Describe(CaptureEntry entry) =>
        $"{entry.Kind,-4} {entry.Method,-6} {entry.Status,3} {entry.Body.Length,9:N0} B  {entry.ContentType ?? "-"}";

    internal static string Text(CaptureEntry entry) => Encoding.UTF8.GetString(entry.Body);

    private sealed record EntryIndex(string Kind, string Method, string Path, int Status, string? ContentType, DateTimeOffset At, string BodyName);
}

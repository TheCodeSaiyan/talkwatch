using System.Text;
using TalkWatch.Capture;

namespace TalkWatch.Capture.Tests;

public sealed class CaptureArchiveTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"talkwatch-{Guid.NewGuid():N}.twcap");
    private readonly CaptureKey _key = CaptureKey.Parse(CaptureKey.Generate());

    public void Dispose() => File.Delete(_file);

    private static CaptureArchive Sample(string body = """{"from":"+44 20 7123 4567"}""") =>
        new(new CaptureManifest(DateTimeOffset.Parse("2026-09-29T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture), "har", "4.3.6", "4.1.0", null),
            [
                new CaptureEntry("http", "GET", "/proxy/talk/api/calls?page=1", 200, "application/json", DateTimeOffset.UnixEpoch, Encoding.UTF8.GetBytes(body)),
                new CaptureEntry("ws", "receive", "/proxy/talk/ws", 101, null, DateTimeOffset.UnixEpoch, [1, 2, 3]),
            ]);

    [Fact]
    public void An_archive_reads_back_exactly_as_it_was_written()
    {
        Sample().Save(_file, _key);

        var loaded = CaptureArchive.Load(_file, _key);

        Assert.Equal("4.1.0", loaded.Manifest.TalkVersion);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.Equal("/proxy/talk/api/calls?page=1", loaded.Entries[0].Path);
        Assert.Equal("""{"from":"+44 20 7123 4567"}""", Encoding.UTF8.GetString(loaded.Entries[0].Body));
        Assert.Equal([1, 2, 3], loaded.Entries[1].Body);
    }

    [Fact]
    public void Nothing_captured_is_readable_in_the_file()
    {
        const string marker = "PLAINTEXT-MARKER-7f3a";
        Sample(marker).Save(_file, _key);

        var bytes = File.ReadAllBytes(_file);

        Assert.DoesNotContain(marker, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.DoesNotContain("proxy/talk", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Another_key_cannot_open_it()
    {
        Sample().Save(_file, _key);

        Assert.Throws<InvalidDataException>(() => CaptureArchive.Load(_file, CaptureKey.Parse(CaptureKey.Generate())));
    }

    [Fact]
    public void An_altered_file_is_refused()
    {
        Sample().Save(_file, _key);
        var bytes = File.ReadAllBytes(_file);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(_file, bytes);

        Assert.Throws<InvalidDataException>(() => CaptureArchive.Load(_file, _key));
    }

    [Fact]
    public void The_derived_keys_differ_from_each_other()
    {
        Assert.NotEqual(_key.EncryptionKey, _key.PseudonymKey);
        Assert.Equal(_key.EncryptionKey, _key.EncryptionKey);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void A_malformed_key_is_refused(string value) =>
        Assert.Throws<InvalidOperationException>(() => CaptureKey.Parse(value));
}

using System.Security.Cryptography;
using TalkWatch.FixtureGuard;

namespace TalkWatch.FixtureGuard.Tests;

public sealed class GuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("talkwatch-guard-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return relativePath;
    }

    private string WriteBytes(string relativePath, byte[] content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return relativePath;
    }

    private static IEnumerable<Finding> ScanLine(string line) => Guard.ScanText("f.json", [line], new HashSet<string>());

    [Fact]
    public void A_real_number_in_a_fixture_is_reported_with_its_line()
    {
        var path = Write("tests/fixtures/calls.json", "{\n  \"from\": \"+44 20 7946 0001\",\n  \"to\": \"+44 20 7123 4567\"\n}");

        var finding = Assert.Single(Guard.Scan(_root, [path]));

        Assert.Equal(3, finding.Line);
        Assert.Equal("+44 20 7123 4567", finding.Value);
    }

    [Fact]
    public void Fictional_numbers_pass() =>
        Assert.Empty(ScanLine("""{"from":"+447700900123","to":"01632 960001","did":"+1 212 555 0142"}"""));

    [Theory]
    [InlineData("""{"at":"2026-09-29T12:34:56.123Z"}""")]
    [InlineData("""{"at":"2026-09-29 12:34:56"}""")]
    [InlineData("""{"id":"123e4567-e89b-12d3-a456-426614174000"}""")]
    [InlineData("""{"extension":"1001","group":"600"}""")]
    [InlineData("""{"duration":12345678}""")]
    [InlineData("""{"ip":"192.168.100.200","sip":"203.0.113.250"}""")]
    public void Dates_ids_and_short_numbers_are_not_mistaken_for_phone_numbers(string line) => Assert.Empty(ScanLine(line));

    [Fact]
    public void A_number_written_next_to_other_text_is_still_found() =>
        Assert.Single(ScanLine("""{"note":"call-back on 07911 123456 please"}"""));

    [Theory]
    [InlineData("/api/calls?number=%2B442071234567")]
    [InlineData("""{"key":"user447911123456"}""")]
    [InlineData("""{"tel":"tel:+442071234567"}""")]
    public void A_number_glued_to_letters_is_still_found(string line) => Assert.Single(ScanLine(line));

    [Theory]
    [InlineData("""{"started":1759147200}""")]
    [InlineData("""{"started_ms": 1759147200123, "ended": 1759147260}""")]
    [InlineData("""{"times":[1759147200,1759147260]}""")]
    [InlineData("""{"started":1759147200.123,"duration":12.345678901}""")]
    [InlineData("""{"rx_rate":15151660586.53,"capacity":49898475795712.125,"load":[564564564564565.4,2085418752085.25]}""")]
    [InlineData("""{"ratio":6.02214076e23}""")]
    [InlineData("""{"received_at":"1759194592","createdAt":"1759194592123"}""")]
    public void Epoch_times_sent_as_json_numbers_pass(string line) => Assert.Empty(ScanLine(line));

    [Theory]
    [InlineData("""{"number":791112345}""")]       // nine digits: no timestamp is that long
    [InlineData("""{"number":7911123456}""")]      // after 2100 as seconds: not a time
    [InlineData("""{"number":447911123456}""")]    // twelve digits: not seconds, milliseconds or microseconds
    [InlineData("""{"number":"1759147200"}""")]    // in a string, it could be a number without its 0
    [InlineData("""{"number":"2071234567.5"}""")]  // a decimal in a string is not a JSON number
    [InlineData("""{"number":"15151660586.53"}""")] // nor is a long one
    [InlineData("""{"received_at":"1759194592 ext"}""")] // a time-named field must hold only the time
    [InlineData("""{"caller":"1759194592"}""")]         // and the field must be named as a time
    public void Other_digit_runs_are_still_reported(string line) => Assert.Single(ScanLine(line));

    [Fact]
    public void A_digit_run_in_a_string_is_reported_until_it_is_allowed()
    {
        var path = Write("tests/fixtures/calls.json", """{"started":"1759147200"}""");
        Assert.Single(Guard.Scan(_root, [path]));

        Write(Guard.AllowListPath, "# epoch seconds, not a phone number\n1759147200\n");
        Assert.Empty(Guard.Scan(_root, [path]));
    }

    [Fact]
    public void Files_outside_the_fixtures_directory_are_ignored()
    {
        var path = Write("docs/notes.md", "Ring +44 20 7123 4567");
        Assert.Empty(Guard.Scan(_root, [path]));
    }

    [Fact]
    public void Audio_must_be_listed_in_the_manifest_with_its_hash()
    {
        byte[] audio = [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0];
        var path = WriteBytes("tests/fixtures/audio/vm1.wav", audio);

        Assert.Single(Guard.Scan(_root, [path]));

        var hash = Convert.ToHexStringLower(SHA256.HashData(audio));
        Write(Guard.AudioManifestPath, $"{hash}  {path}\n");
        Assert.Empty(Guard.Scan(_root, [path]));
    }

    [Fact]
    public void Audio_changed_after_it_was_listed_is_reported()
    {
        var path = WriteBytes("tests/fixtures/audio/vm1.wav", [1, 2, 3]);
        Write(Guard.AudioManifestPath, $"{Convert.ToHexStringLower(SHA256.HashData(new byte[] { 9, 9, 9 }))}  {path}\n");

        Assert.Single(Guard.Scan(_root, [path]));
    }

    [Fact]
    public void Unknown_binary_files_are_refused()
    {
        var path = WriteBytes("tests/fixtures/capture.bin", [0x50, 0x4b, 0, 0]);
        Assert.Single(Guard.Scan(_root, [path]));
    }
}

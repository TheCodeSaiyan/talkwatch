using System.Text;
using TalkWatch.Capture;
using TalkWatch.FixtureGuard;

namespace TalkWatch.Capture.Tests;

public sealed class FixtureWriterTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("talkwatch-fixtures-").FullName;
    private readonly CaptureKey _key = CaptureKey.Parse(CaptureKey.Generate());

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Out(string name) => Path.Combine(_root, "tests", "fixtures", name);

    private static CaptureEntry Entry(string path, string? type, byte[] body, string kind = "http") =>
        new(kind, kind == "ws" ? "receive" : "GET", path, 200, type, DateTimeOffset.UnixEpoch, body);

    private static CaptureArchive Capture() => new(
        new CaptureManifest(DateTimeOffset.UnixEpoch, "har", null, null, null),
        [
            Entry("/proxy/talk/api/calls?number=%2B442071234567", "application/json",
                """{"calls":[{"from":"+44 20 7123 4567","name":"Jane Smith","to":"07911 123456"}]}"""u8.ToArray()),
            Entry("/proxy/talk/api/calls.csv", "text/csv", "From,Name\n+442071234567,Jane Smith\n"u8.ToArray()),
            Entry("/proxy/talk/ws", null, """{"event":"ringing","from":"+447911123456"}"""u8.ToArray(), kind: "ws"),
            Entry("/proxy/talk/api/voicemail/1/audio", "audio/wav", SyntheticAudio.Build(1, 8000, 16, new byte[16000])),
            Entry("/proxy/talk/api/blob", "application/octet-stream", [0, 1, 2, 3]),
            Entry("/proxy/talk/api/call_log/recording/abc", "audio/mpeg", SyntheticMp3Tests.Frames(10)),
        ]);

    [Fact]
    public void A_capture_becomes_fixtures_the_guard_accepts()
    {
        var result = FixtureWriter.Write(_root, Out("sample"), [Capture()], _key);

        Assert.Empty(result.Findings);
        Assert.Equal((5, 2, 1), (result.Files, result.AudioFiles, result.Dropped));

        var paths = Directory.EnumerateFiles(Out("sample"), "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(_root, p).Replace('\\', '/')).ToList();
        Assert.Empty(Guard.Scan(_root, paths));

        var everything = string.Join("\n", paths.Where(p => !p.EndsWith(".wav", StringComparison.Ordinal) && !p.EndsWith(".mp3", StringComparison.Ordinal))
            .Select(p => File.ReadAllText(Path.Combine(_root, p))));
        Assert.DoesNotContain("Jane", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("7123 4567", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("2071234567", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("7911", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Synthetic_audio_is_listed_in_the_manifest_with_its_hash()
    {
        FixtureWriter.Write(_root, Out("sample"), [Capture()], _key);

        var manifest = File.ReadAllText(Path.Combine(_root, Guard.AudioManifestPath));

        Assert.Contains("tests/fixtures/sample/audio/0004.wav", manifest, StringComparison.Ordinal);
        Assert.Contains("tests/fixtures/sample/audio/0006.mp3", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_outside_the_fixtures_directory_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => FixtureWriter.Write(_root, Path.Combine(_root, "docs"), [Capture()], _key));

    [Fact]
    public void An_existing_directory_with_files_in_it_is_refused()
    {
        Directory.CreateDirectory(Out("sample"));
        File.WriteAllText(Path.Combine(Out("sample"), "keep.json"), "{}");

        Assert.Throws<InvalidOperationException>(() => FixtureWriter.Write(_root, Out("sample"), [Capture()], _key));
    }

    [Fact]
    public void A_failure_part_way_leaves_nothing_behind()
    {
        byte[] brokenMp3 = [.. "ID3"u8.ToArray(), 3, 0, 0, 0, 0, 0, 0, 0xAA, 0xBB];
        var capture = new CaptureArchive(Capture().Manifest, [.. Capture().Entries, Entry("/proxy/talk/api/broken", "audio/mpeg", brokenMp3)]);

        Assert.Throws<NotSupportedException>(() => FixtureWriter.Write(_root, Out("sample"), [capture], _key));

        Assert.False(Directory.Exists(Out("sample")));
        Assert.False(File.Exists(Path.Combine(_root, Guard.AudioManifestPath)));
    }

    [Fact]
    public void A_refused_directory_is_left_exactly_as_it_was()
    {
        Directory.CreateDirectory(Out("sample"));
        File.WriteAllText(Path.Combine(Out("sample"), "keep.json"), "{}");

        Assert.Throws<InvalidOperationException>(() => FixtureWriter.Write(_root, Out("sample"), [Capture()], _key));

        Assert.True(File.Exists(Path.Combine(Out("sample"), "keep.json")));
    }

    [Fact]
    public void Selection_keeps_included_paths_once_each()
    {
        var (selected, outside, duplicates) = FixtureWriter.Select([Capture(), Capture()], ["/proxy/talk/api/calls"]);

        Assert.Equal(["/proxy/talk/api/calls?number=%2B442071234567", "/proxy/talk/api/calls.csv"], selected.SelectMany(a => a.Entries).Select(e => e.Path));
        Assert.Equal((8, 2), (outside, duplicates));
    }

    [Fact]
    public void Two_captures_share_one_mapping()
    {
        FixtureWriter.Write(_root, Out("pair"), [Capture(), Capture()], _key);

        var index = File.ReadAllText(Path.Combine(Out("pair"), "index.json"), Encoding.UTF8);
        var paths = System.Text.Json.JsonDocument.Parse(index).RootElement.EnumerateArray().Select(e => e.GetProperty("path").GetString()).ToList();

        Assert.Equal(paths[0], paths[6]);
    }
}

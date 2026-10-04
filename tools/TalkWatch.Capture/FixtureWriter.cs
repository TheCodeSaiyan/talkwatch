using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TalkWatch.FixtureGuard;

namespace TalkWatch.Capture;

public sealed record FixtureResult(int Files, int AudioFiles, int Dropped, int NumbersReplaced, IReadOnlyList<Finding> Findings);

/// <summary>
/// Turns one or more capture archives into committed fixtures, then runs the fixture guard over what it wrote.
/// If the guard finds anything, the output is deleted, so a failed run never leaves real data in the tree.
/// </summary>
public static partial class FixtureWriter
{
    private static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex Unsafe();

    /// <summary>
    /// Narrows captures to what is worth committing: entries whose path starts with one of <paramref name="includePrefixes"/>
    /// (all of them when there are none), and each distinct response once. A capture repeats many requests, and a
    /// repeated identical response adds size to the repository and nothing to the tests.
    /// </summary>
    public static (IReadOnlyList<CaptureArchive> Archives, int OutsideScope, int Duplicates) Select(
        IEnumerable<CaptureArchive> archives, IReadOnlyCollection<string> includePrefixes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int outside = 0, duplicates = 0;
        var selected = new List<CaptureArchive>();
        foreach (var archive in archives)
        {
            var kept = new List<CaptureEntry>();
            foreach (var entry in archive.Entries)
            {
                if (includePrefixes.Count > 0 && !includePrefixes.Any(p => entry.Path.StartsWith(p, StringComparison.Ordinal)))
                {
                    outside++;
                }
                else if (!seen.Add($"{entry.Kind} {entry.Method} {entry.Path} {Convert.ToHexString(SHA256.HashData(entry.Body))}"))
                {
                    duplicates++;
                }
                else
                {
                    kept.Add(entry);
                }
            }

            selected.Add(new CaptureArchive(archive.Manifest, kept));
        }

        return (selected, outside, duplicates);
    }

    public static FixtureResult Write(string repositoryRoot, string outputDirectory, IEnumerable<CaptureArchive> archives, CaptureKey key)
    {
        // Checked before anything is written, so the clean-up below can never touch a directory it did not fill.
        var relativeOut = System.IO.Path.GetRelativePath(repositoryRoot, System.IO.Path.GetFullPath(outputDirectory)).Replace('\\', '/').TrimEnd('/') + "/";
        if (!relativeOut.StartsWith(Guard.FixturesDirectory, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Fixtures must be written under {Guard.FixturesDirectory}, not {relativeOut}.");
        }

        if (Directory.Exists(outputDirectory) && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
        {
            throw new InvalidOperationException($"{relativeOut} is not empty. Choose a new directory, so nothing is merged with an earlier run by mistake.");
        }

        var manifestPath = System.IO.Path.Combine(repositoryRoot, Guard.AudioManifestPath);
        var manifestBefore = File.Exists(manifestPath) ? File.ReadAllBytes(manifestPath) : null;
        var existedBefore = Directory.Exists(outputDirectory);
        FixtureResult result;
        try
        {
            result = WriteUnchecked(repositoryRoot, outputDirectory, relativeOut, archives, key);
        }
        catch
        {
            Undo();
            throw;
        }

        // Refused by the guard: undone exactly as a failure is, so both leave the tree as it was.
        if (result.Findings.Count > 0)
        {
            Undo();
        }

        return result;

        // Files the guard has not passed must not stay in the tree. The directory was empty or absent before this
        // run, so everything in it now came from this run, and the audio manifest goes back to its exact bytes.
        void Undo()
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
                if (existedBefore)
                {
                    Directory.CreateDirectory(outputDirectory);
                }
            }

            if (manifestBefore is null)
            {
                File.Delete(manifestPath);
            }
            else
            {
                File.WriteAllBytes(manifestPath, manifestBefore);
            }
        }
    }

    private static FixtureResult WriteUnchecked(string repositoryRoot, string outputDirectory, string relativeOut, IEnumerable<CaptureArchive> archives, CaptureKey key)
    {
        var pseudonymiser = new Pseudonymiser(key.PseudonymKey);
        var written = new List<string>();
        var audio = new List<(string Path, string Hash)>();
        var index = new List<object>();
        var dropped = 0;
        var number = 0;

        foreach (var archive in archives)
        {
            foreach (var entry in archive.Entries)
            {
                number++;
                var path = pseudonymiser.Path(entry.Path);
                var stem = $"{number:D4}-{entry.Kind}-{Slug(path)}";
                string? file = null;

                if (SyntheticAudio.IsWav(entry.Body))
                {
                    file = $"audio/{number:D4}.wav";
                    var bytes = SyntheticAudio.ReplaceWav(entry.Body);
                    Save(outputDirectory, file, bytes);
                    audio.Add((relativeOut + file, Convert.ToHexStringLower(SHA256.HashData(bytes))));
                }
                else if (SyntheticMp3.IsMp3(entry.Body))
                {
                    file = $"audio/{number:D4}.mp3";
                    var bytes = SyntheticMp3.Replace(entry.Body);
                    Save(outputDirectory, file, bytes);
                    audio.Add((relativeOut + file, Convert.ToHexStringLower(SHA256.HashData(bytes))));
                }
                else if (IsText(entry))
                {
                    var text = Encoding.UTF8.GetString(entry.Body);
                    var (content, extension) = IsJson(text)
                        ? (pseudonymiser.Json(text), ".json")
                        : IsCsv(entry) ? (pseudonymiser.Text(text, isCsv: true), ".csv") : (pseudonymiser.Text(text, isCsv: false), ".txt");
                    file = stem + extension;
                    Save(outputDirectory, file, Encoding.UTF8.GetBytes(content));
                }
                else
                {
                    // Unknown binary content cannot be checked, so it is left out rather than committed blind.
                    dropped++;
                }

                if (file is not null)
                {
                    written.Add(relativeOut + file);
                }

                index.Add(new { entry.Kind, entry.Method, Path = path, entry.Status, entry.ContentType, At = entry.At.ToString("O"), File = file });
            }
        }

        var indexFile = "index.json";
        Save(outputDirectory, indexFile, JsonSerializer.SerializeToUtf8Bytes(index, Indented));
        written.Add(relativeOut + indexFile);

        // Property names only, never values: this is what a person reads to check nothing personal was missed.
        Save(outputDirectory, "fields.txt", Encoding.UTF8.GetBytes(string.Join('\n', pseudonymiser.FieldNames) + "\n"));
        written.Add(relativeOut + "fields.txt");

        var manifestPath = System.IO.Path.Combine(repositoryRoot, Guard.AudioManifestPath);
        var manifestLines = audio.Select(a => $"{a.Hash}  {a.Path}").ToList();
        if (manifestLines.Count > 0)
        {
            File.AppendAllLines(manifestPath, manifestLines);
        }

        var findings = Guard.Scan(repositoryRoot, written.Concat(audio.Select(a => a.Path))).ToList();
        findings.AddRange(ProseLeft(repositoryRoot, written));
        return new FixtureResult(written.Count - 2, audio.Count, dropped, pseudonymiser.NumbersReplaced, findings);
    }

    // The phone-number guard cannot see words. This is its counterpart for prose: any JSON string of three or more words
    // that is not one of the pseudonymiser's own fakes means a rule missed something, so the run is refused.
    private static IEnumerable<Finding> ProseLeft(string repositoryRoot, IEnumerable<string> written)
    {
        foreach (var path in written.Where(p => p.EndsWith(".json", StringComparison.Ordinal) && !p.EndsWith("/index.json", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(System.IO.Path.Combine(repositoryRoot, path)));
            var fields = new SortedSet<string>(StringComparer.Ordinal);
            Collect(document.RootElement, "$", fields);
            foreach (var field in fields)
            {
                yield return new Finding(path, 0, field, $"prose not replaced in {field}");
            }
        }

        static void Collect(JsonElement element, string at, SortedSet<string> fields)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var child in element.EnumerateObject())
                    {
                        Collect(child.Value, $"{at}.{child.Name}", fields);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        Collect(item, at + "[]", fields);
                    }

                    break;
                case JsonValueKind.String when Pseudonymiser.IsUnreplacedProse(element.GetString()!):
                    fields.Add(at);
                    break;
            }
        }
    }

    private static void Save(string directory, string relative, byte[] content)
    {
        var full = System.IO.Path.Combine(directory, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
    }

    private static string Slug(string path)
    {
        var slug = Unsafe().Replace(path.Split('?')[0], "-").Trim('-');
        return slug.Length > 60 ? slug[^60..].TrimStart('-') : slug.Length == 0 ? "root" : slug;
    }

    private static bool IsText(CaptureEntry entry) =>
        entry.Kind == "ws"
        || entry.ContentType is { } type && (type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.Contains("csv", StringComparison.OrdinalIgnoreCase))
        || (entry.ContentType is null && !entry.Body.Contains((byte)0));

    private static bool IsCsv(CaptureEntry entry) =>
        entry.ContentType?.Contains("csv", StringComparison.OrdinalIgnoreCase) == true || entry.Path.Split('?')[0].EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

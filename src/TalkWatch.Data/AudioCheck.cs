using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;

namespace TalkWatch.Data;

/// <param name="Checked">Copied files looked at.</param>
/// <param name="Missing">Rows saying a file is copied, with no file there. Repaired: copied again from the console.</param>
/// <param name="Damaged">Files whose checksum no longer matches. Repaired the same way.</param>
/// <param name="Orphans">Files no row refers to, by path: reported, not removed.</param>
public sealed record AudioCheckResult(int Checked, int Missing, int Damaged, IReadOnlyList<string> Orphans)
{
    public bool Clean => Missing == 0 && Damaged == 0 && Orphans.Count == 0;
}

/// <summary>
/// Holds the audio index and the audio on disk to each other, as they can drift apart after a restore: the database
/// dump and the file backup are taken minutes apart. A copied file that is missing or damaged is marked to be copied
/// again, which the next poll does while the console still has it. A file no row refers to is reported and left: its
/// call comes back from the console at the next poll, and the copy is then used again.
/// </summary>
public sealed class AudioCheck(TalkWatchDbContext db, AudioStore store, TimeProvider clock)
{
    /// <param name="checksums">Read every file and compare its SHA-256, not only that it is there. Slow on a large store.</param>
    public async Task<AudioCheckResult> RunAsync(bool checksums, CancellationToken cancellationToken)
    {
        var copied = await db.AudioFiles.Where(a => a.State == AudioState.Copied).ToListAsync(cancellationToken);
        int missing = 0, damaged = 0;
        foreach (var audio in copied)
        {
            var path = audio.RelativePath is { } relative ? Path.Combine(store.Root, relative) : null;
            var problem = path is null || !File.Exists(path)
                ? "missing"
                : checksums && !string.Equals(await HashAsync(path, cancellationToken), audio.Sha256, StringComparison.OrdinalIgnoreCase)
                    ? "damaged"
                    : null;
            if (problem is null)
            {
                continue;
            }

            if (problem == "missing")
            {
                missing++;
            }
            else
            {
                damaged++;
            }

            // Back to the copier: Failed with no attempts spent is fetched again at the next poll.
            audio.State = AudioState.Failed;
            audio.Attempts = 0;
            audio.LastAttemptAt = clock.GetUtcNow();
            audio.LastError = $"The copy was {problem} when checked; copying it again.";
        }

        await db.SaveChangesAsync(cancellationToken);

        // A file with a row is not an orphan, whatever the row's state: one marked just now to copy again still has it.
        var known = (await db.AudioFiles.Where(a => a.RelativePath != null).Select(a => a.RelativePath!).ToListAsync(cancellationToken))
            .Select(p => p.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var orphans = Directory.Exists(store.Root)
            ? Directory.EnumerateFiles(store.Root, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(store.Root, f).Replace('\\', '/'))
                .Where(f => !f.StartsWith(".incoming/", StringComparison.Ordinal) && !known.Contains(f))
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

        return new AudioCheckResult(copied.Count, missing, damaged, orphans);
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellationToken));
    }
}

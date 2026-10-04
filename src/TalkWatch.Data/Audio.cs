using System.Security.Cryptography;

namespace TalkWatch.Data;

public enum AudioKind
{
    Recording,
    Voicemail,
}

public enum AudioState
{
    /// <summary>Copied into TalkWatch's own storage and verified by checksum.</summary>
    Copied,

    /// <summary>The console no longer has it (404). Consoles prune; not retried.</summary>
    Unavailable,

    /// <summary>The last attempt failed for another reason; retried up to <see cref="AudioCopier.MaxAttempts"/> times.</summary>
    Failed,

    /// <summary>Copied once, then removed under the retention policy. Kept as a row so it is not copied again.</summary>
    Expired,
}

/// <summary>
/// A recording or voicemail TalkWatch holds a copy of, or tried to. Copied because the console prunes audio and
/// firmware updates have wiped app data before.
/// </summary>
public sealed class AudioFile
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public Guid CallId { get; set; }
    public AudioKind Kind { get; set; }
    public AudioState State { get; set; }

    /// <summary>Relative to the audio root, and only ever built by TalkWatch, never taken from a request.</summary>
    public string? RelativePath { get; set; }

    public string? Sha256 { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }
    public DateTimeOffset? CopiedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Who did something sensitive, and to what: every audio play, and later every grant change.</summary>
public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public Guid SiteId { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? UserId { get; set; }
    public required string Action { get; set; }
    public required string TargetType { get; set; }
    public required string TargetId { get; set; }

    /// <summary>What changed, in words: the role given, the line and access granted. Never a password.</summary>
    public string? Detail { get; set; }
}

/// <summary>
/// Audio files on disk under one root. A file is written under a temporary name and moved into place only once it
/// is complete and hashed, so a half-written file is never served.
/// </summary>
public sealed class AudioStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public async Task<(string RelativePath, string Sha256, long Size)> SaveAsync(Stream content, AudioKind kind, string callUuid, DateTimeOffset callTime, string extension, CancellationToken cancellationToken)
    {
        var safeName = string.Concat(callUuid.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-'));
        if (safeName.Length == 0)
        {
            throw new ArgumentException("The call id has nothing usable as a file name.", nameof(callUuid));
        }

        var relative = Path.Combine(kind == AudioKind.Recording ? "recordings" : "voicemail",
            callTime.UtcDateTime.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture),
            callTime.UtcDateTime.ToString("MM", System.Globalization.CultureInfo.InvariantCulture),
            safeName + extension).Replace('\\', '/');
        var final = Path.Combine(Root, relative);
        var temporary = Path.Combine(Root, ".incoming", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(temporary)!);
        Directory.CreateDirectory(Path.GetDirectoryName(final)!);

        string hash;
        long size;
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            size = file.Length;
            hash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        File.Move(temporary, final, overwrite: true);
        return (relative, hash, size);
    }

    public Stream OpenRead(string relativePath) =>
        new FileStream(Inside(relativePath), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);

    /// <summary>Removes a file, if it is there; one already gone is not an error.</summary>
    public void Delete(string relativePath) => File.Delete(Inside(relativePath));

    private string Inside(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(Root, relativePath));
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            ? full
            : throw new InvalidOperationException("An audio path left the audio root.");
    }
}

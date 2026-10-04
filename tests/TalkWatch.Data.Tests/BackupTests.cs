using Microsoft.EntityFrameworkCore;
using Npgsql;
using TalkWatch.Core.Calls;
using TalkWatch.Core.Talk;
using TalkWatch.Replay;

namespace TalkWatch.Data.Tests;

public sealed class BackupTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Install(string Connection, TalkWatchDbContext Db, Guid SiteId, TalkClient Talk, AudioStore Store);

    /// <summary>An install with every fixture call stored and every recording the fixtures hold copied.</summary>
    private async Task<Install> InstallAsync()
    {
        var connection = await postgres.NewDatabaseAsync();
        var siteId = Guid.NewGuid();
        var db = PostgresFixture.Context(connection, AccessScope.System(siteId));
        db.Sites.Add(new Site { Id = siteId, Name = "Test site", DefaultRegion = "GB", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Ct);
        var talk = new TalkClient(new FixtureConsole(FixtureConsole.DefaultDirectory).CreateClient());
        await talk.SignInAsync(FixtureConsole.Username, FixtureConsole.Password, Ct);
        await new CallLogIngestor(talk, db, siteId, new NumberNormaliser("GB"), TimeProvider.System) { PageSize = 100 }.IngestAsync(Ct);
        var store = new AudioStore(Directory.CreateTempSubdirectory("talkwatch-backup-").FullName);
        await new AudioCopier(talk, db, siteId, store, TimeProvider.System) { MaxPerRun = 1000 }.CopyRecordingsAsync(Ct);
        return new Install(connection, db, siteId, talk, store);
    }

    private static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public async Task A_restore_from_pg_dump_and_the_audio_files_gives_a_whole_install_again()
    {
        var original = await InstallAsync();
        var database = new NpgsqlConnectionStringBuilder(original.Connection).Database!;
        var restoredName = database + "r";
        var calls = await original.Db.Calls.CountAsync(Ct);
        var copied = await original.Db.AudioFiles.CountAsync(a => a.State == AudioState.Copied, Ct);
        Assert.True(copied > 1, "The fixtures should give more than one recording to copy.");

        // The backup: a pg_dump of the database, and the audio volume. The restore: both, onto a clean database and folder,
        // with one recording missing from the files, as when the two backups were taken minutes apart.
        await postgres.ExecAsync("pg_dump", "-U", "postgres", "-Fc", "-f", $"/tmp/{database}.dump", database);
        await postgres.ExecAsync("createdb", "-U", "postgres", restoredName);
        await postgres.ExecAsync("pg_restore", "-U", "postgres", "--no-owner", "-d", restoredName, $"/tmp/{database}.dump");
        var restoredStore = new AudioStore(Directory.CreateTempSubdirectory("talkwatch-restored-").FullName);
        CopyTree(original.Store.Root, restoredStore.Root);
        var lost = await original.Db.AudioFiles.Where(a => a.State == AudioState.Copied).Select(a => a.RelativePath!).FirstAsync(Ct);
        File.Delete(Path.Combine(restoredStore.Root, lost));

        var connection = new NpgsqlConnectionStringBuilder(original.Connection) { Database = restoredName }.ConnectionString;
        await using var restored = PostgresFixture.Context(connection, AccessScope.System(original.SiteId));
        await restored.Database.MigrateAsync(Ct);
        var found = await new AudioCheck(restored, restoredStore, TimeProvider.System).RunAsync(checksums: true, Ct);
        await new AudioCopier(original.Talk, restored, original.SiteId, restoredStore, TimeProvider.System).CopyRecordingsAsync(Ct);
        var after = await new AudioCheck(restored, restoredStore, TimeProvider.System).RunAsync(checksums: true, Ct);

        Assert.Equal(calls, await restored.Calls.CountAsync(Ct));
        Assert.Equal((1, 0), (found.Missing, found.Damaged));
        Assert.Empty(found.Orphans);
        Assert.True(after.Clean, $"After the next copy: {after}");
        Assert.Equal(copied, await restored.AudioFiles.CountAsync(a => a.State == AudioState.Copied, Ct));
    }

    [Fact]
    public async Task The_check_finds_missing_damaged_and_unreferred_files_and_repairs_the_first_two()
    {
        var install = await InstallAsync();
        var files = await install.Db.AudioFiles.Where(a => a.State == AudioState.Copied).OrderBy(a => a.RelativePath).ToListAsync(Ct);
        File.Delete(Path.Combine(install.Store.Root, files[0].RelativePath!));
        await File.WriteAllBytesAsync(Path.Combine(install.Store.Root, files[1].RelativePath!), [1, 2, 3], Ct);
        Directory.CreateDirectory(Path.Combine(install.Store.Root, "recordings", "2020", "01"));
        await File.WriteAllBytesAsync(Path.Combine(install.Store.Root, "recordings", "2020", "01", "stray.mp3"), [0], Ct);
        Directory.CreateDirectory(Path.Combine(install.Store.Root, ".incoming"));
        await File.WriteAllBytesAsync(Path.Combine(install.Store.Root, ".incoming", "half-written"), [0], Ct);

        var quick = await new AudioCheck(install.Db, install.Store, TimeProvider.System).RunAsync(checksums: false, Ct);
        var full = await new AudioCheck(install.Db, install.Store, TimeProvider.System).RunAsync(checksums: true, Ct);
        install.Db.ChangeTracker.Clear();

        // Existence only spots the missing file; checksums spot the damaged one as well.
        Assert.Equal((1, 0), (quick.Missing, quick.Damaged));
        Assert.Equal((0, 1), (full.Missing, full.Damaged));
        Assert.Equal(["recordings/2020/01/stray.mp3"], full.Orphans);
        var repaired = await install.Db.AudioFiles.Where(a => a.Id == files[0].Id || a.Id == files[1].Id).ToListAsync(Ct);
        Assert.All(repaired, a => Assert.Equal((AudioState.Failed, 0), (a.State, a.Attempts)));

        await new AudioCopier(install.Talk, install.Db, install.SiteId, install.Store, TimeProvider.System).CopyRecordingsAsync(Ct);
        var after = await new AudioCheck(install.Db, install.Store, TimeProvider.System).RunAsync(checksums: true, Ct);
        Assert.Equal((0, 0), (after.Missing, after.Damaged));
    }
}

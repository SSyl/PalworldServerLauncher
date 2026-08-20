using System.Globalization;
using System.Threading;
using System.IO.Compression;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

public class BackupServiceTests
{
    /// <summary>A minimal installed-server tree: one world folder plus the two config files a restore needs.</summary>
    private static string WriteFakeInstall(string worldId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pal_bk_{Guid.NewGuid():N}");
        var saved = Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved");
        var world = Path.Combine(saved, "SaveGames", "0", worldId);
        var cfg = Path.Combine(saved, "Config", "WindowsServer");
        Directory.CreateDirectory(world);
        Directory.CreateDirectory(cfg);
        File.WriteAllText(Path.Combine(world, "Level.sav"), "level");
        File.WriteAllText(Path.Combine(cfg, "PalWorldSettings.ini"), "[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=()\r\n");
        File.WriteAllText(Path.Combine(cfg, "GameUserSettings.ini"),
            $"[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName={worldId}\r\n");
        File.WriteAllText(Path.Combine(cfg, "Engine.ini"), "[Core.Log]\r\n");
        return root;
    }

    /// <summary>Without GameUserSettings.ini a restore into a fresh install loses the world: nothing names the
    /// save folder, so the server generates a new GUID and comes up empty (issue #9 groundwork).</summary>
    [Fact]
    public async Task BackupNow_archives_the_world_and_both_config_files()
    {
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var config = new LauncherConfig { ServerRoot = root };
            var service = new BackupService(config, new Logger(verbose: false));

            var zipPath = await service.BackupNowAsync(BackupReason.Manual, rest: null, serverRunning: false);

            Assert.NotNull(zipPath);
            using var zip = ZipFile.OpenRead(zipPath!);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains($"SaveGames/0/{worldId}/Level.sav", entries);
            Assert.Contains("Config/WindowsServer/PalWorldSettings.ini", entries);
            Assert.Contains("Config/WindowsServer/GameUserSettings.ini", entries);
            Assert.DoesNotContain("Config/WindowsServer/Engine.ini", entries);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveBackupsDir_uses_default_when_override_blank(string? backupFolder)
    {
        var expected = Path.Combine("C:\\ServerRoot", LauncherConfig.BackupsFolderName);
        Assert.Equal(expected, BackupService.ResolveBackupsDir("C:\\ServerRoot", backupFolder));
    }

    [Fact]
    public void ResolveBackupsDir_uses_the_custom_folder_verbatim_when_set() =>
        Assert.Equal("D:\\My Backups", BackupService.ResolveBackupsDir("C:\\ServerRoot", "D:\\My Backups"));

    [Theory]
    [InlineData("0/ABC/backup/world/2026.07.08/Level.sav", true)]  // Palworld's own rolling backups
    [InlineData("0/ABC/backup", true)]
    [InlineData("0/ABC/Backup/x.sav", true)]                        // case-insensitive
    [InlineData("0/ABC/Level.sav", false)]                          // live world save - keep
    [InlineData("0/ABC/LevelMeta.sav", false)]
    [InlineData("0/ABC/Players/steam_1.sav", false)]                // keep
    [InlineData("0/ABC/backups/x", false)]                          // "backups" != "backup" - keep
    public void ShouldSkipEntry_excludes_only_nested_backup_dirs(string relativePath, bool expected)
    {
        Assert.Equal(expected, BackupService.ShouldSkipEntry(relativePath));
    }

    [Fact]
    public void ShouldSkipEntry_handles_windows_separators()
    {
        Assert.True(BackupService.ShouldSkipEntry(@"0\ABC\backup\world\Level.sav"));
        Assert.False(BackupService.ShouldSkipEntry(@"0\ABC\Level.sav"));
    }

    [Fact]
    public void SelectExpired_returns_backups_older_than_retention()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        var files = new[]
        {
            ("old.zip", now.AddDays(-10)),
            ("edge.zip", now.AddDays(-7).AddMinutes(-1)), // just over 7 days -> expired
            ("fresh.zip", now.AddDays(-1)),
            ("new.zip", now),
        };

        var expired = BackupService.SelectExpired(files, retentionDays: 7, now);

        Assert.Equal(new[] { "edge.zip", "old.zip" }, expired.OrderBy(p => p).ToArray());
    }

    [Fact]
    public void SelectExpired_zero_or_negative_retention_keeps_everything()
    {
        var now = new DateTime(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);
        var files = new[] { ("a.zip", now.AddDays(-100)) };

        Assert.Empty(BackupService.SelectExpired(files, 0, now));
        Assert.Empty(BackupService.SelectExpired(files, -5, now));
    }

    [Fact]
    public void SelectExpired_empty_input_is_empty()
    {
        Assert.Empty(BackupService.SelectExpired(Array.Empty<(string, DateTime)>(), 7, DateTime.UtcNow));
    }

    [Theory]
    [InlineData("palworld-20260708-120000-scheduled.zip", true)]
    [InlineData("palworld-20260708-120000-startup.zip", true)]
    [InlineData("palworld-20260708-120000-shutdown.zip", true)]
    [InlineData("palworld-20260708-120000-manual.zip", false)]   // user-made keeper - never pruned
    [InlineData("palworld-20260708-120000-MANUAL.zip", false)]   // case-insensitive
    [InlineData("my-own-backup.zip", false)]                     // not our naming - never pruned
    [InlineData("palworld-notes.zip", false)]                    // partial match - never pruned
    [InlineData("palworld-20260708-120000-.zip", false)]         // no reason token - not ours
    public void IsPrunableAutoBackup_covers_only_auto_backups(string fileName, bool expected)
    {
        Assert.Equal(expected, BackupService.IsPrunableAutoBackup(fileName));
    }

    [Fact]
    public void A_save_file_open_for_archiving_can_still_be_swapped()
    {
        // Windows blocks delete, delete-then-rename and ReplaceFile against a handle without
        // FileShare.Delete, the contention reported in issue #18.
        var dir = Path.Combine(Path.GetTempPath(), $"pal_backup_share_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var save = Path.Combine(dir, "Level.sav");
            var incoming = Path.Combine(dir, "Level.sav.tmp");
            var discarded = Path.Combine(dir, "Level.sav.bak");
            File.WriteAllText(save, "old world");
            File.WriteAllText(incoming, "new world");

            using (var archiving = BackupService.OpenForArchive(save))
            {
                File.Replace(incoming, save, discarded);

                File.WriteAllText(incoming, "newer world");
                File.Delete(save);
                File.Move(incoming, save);

                // The archive copy still reads the bytes from before the swap.
                using var reader = new StreamReader(archiving);
                Assert.Equal("old world", reader.ReadToEnd());
            }

            Assert.Equal("newer world", File.ReadAllText(save));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task An_unreadable_file_is_left_out_rather_than_archived_as_zero_bytes()
    {
        // A zero-byte Level.sav inside an archive the log calls a success is worse than a failed backup.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var locked = Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved", "SaveGames", "0", worldId, "Locked.sav");
            File.WriteAllText(locked, "unreadable");
            var service = new BackupService(new LauncherConfig { ServerRoot = root }, new Logger(verbose: false));

            string? zipPath;
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                zipPath = await service.BackupNowAsync(BackupReason.Manual, rest: null, serverRunning: false);

            Assert.NotNull(zipPath);
            using var zip = ZipFile.OpenRead(zipPath!);
            var entries = zip.Entries.Select(e => e.FullName).ToList();
            Assert.DoesNotContain($"SaveGames/0/{worldId}/Locked.sav", entries);
            Assert.Contains($"SaveGames/0/{worldId}/Level.sav", entries);
            Assert.All(zip.Entries, e => Assert.True(e.Length > 0, $"{e.FullName} archived empty"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_timestamp_a_zip_cannot_hold_does_not_abandon_the_archive()
    {
        // Zip entries start at 1980, and a file deleted under an open handle reports year 1600.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var save = Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved", "SaveGames", "0", worldId, "Level.sav");
            // 1979 rather than 1601: the latter converts to a pre-epoch Win32 FileTime east of UTC and the
            // setter itself throws there, failing the test on the setup line.
            File.SetLastWriteTime(save, new DateTime(1979, 12, 31, 12, 0, 0));
            var service = new BackupService(new LauncherConfig { ServerRoot = root }, new Logger(verbose: false));

            var zipPath = await service.BackupNowAsync(BackupReason.Scheduled, rest: null, serverRunning: false);

            Assert.NotNull(zipPath);
            using var zip = ZipFile.OpenRead(zipPath!);
            Assert.Contains($"SaveGames/0/{worldId}/Level.sav", zip.Entries.Select(e => e.FullName));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_second_backup_in_the_same_second_cannot_destroy_the_first()
    {
        // Both runs compute the same name. The loser used to delete the winner's finished archive on its
        // way out, which cost a good backup that a caller had already been handed.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var service = new BackupService(new LauncherConfig { ServerRoot = root }, new Logger(verbose: false));
            var backupsDir = BackupService.ResolveBackupsDir(root, null);

            var first = await service.BackupNowAsync(BackupReason.Shutdown, rest: null, serverRunning: false);
            var second = await service.BackupNowAsync(BackupReason.Shutdown, rest: null, serverRunning: false);

            Assert.NotNull(first);
            Assert.True(File.Exists(first!), "the first archive was deleted by the second run");
            using var zip = ZipFile.OpenRead(first!);
            Assert.Contains($"SaveGames/0/{worldId}/Level.sav", zip.Entries.Select(e => e.FullName));
            Assert.Empty(Directory.EnumerateFiles(backupsDir, "*.partial"));
            Assert.True(second is null || second == first || File.Exists(second));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_missing_config_file_is_reported_rather_than_quietly_absent()
    {
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        var lines = new List<string>();
        try
        {
            File.Delete(Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved", "Config", "WindowsServer", "GameUserSettings.ini"));
            var logger = new Logger(verbose: false);
            logger.LineForUi += entry => lines.Add(entry.Text);
            var service = new BackupService(new LauncherConfig { ServerRoot = root }, logger);

            var zipPath = await service.BackupNowAsync(BackupReason.Manual, rest: null, serverRunning: false);

            Assert.NotNull(zipPath);
            Assert.Contains(lines, l => l.Contains("GameUserSettings.ini") && l.Contains("isn't in this archive"));
            Assert.Contains(lines, l => l.Contains("1 file(s) are NOT in it"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("palworld-20260101-120000-scheduled.zip.partial", true)]
    [InlineData("palworld-20260101-120000-manual.zip.partial", true)]
    [InlineData("palworld-my-own-archive.zip.partial", false)]
    [InlineData("palworld-notes.partial", false)]
    [InlineData("myworld.zip.partial", false)]
    [InlineData("palworld-20260101-120000-scheduled.zip", false)]
    public void IsOurPartialArchive_only_matches_our_own_naming(string fileName, bool expected) =>
        Assert.Equal(expected, BackupService.IsOurPartialArchive(fileName));

    [Fact]
    public async Task An_undeletable_backup_does_not_stop_the_others_being_pruned()
    {
        // A user marks one auto-backup read-only to keep it. Its delete throws every run, which used to
        // abandon the rest of the retention sweep, so everything expired behind it was kept forever.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var backupsDir = BackupService.ResolveBackupsDir(root, null);
            Directory.CreateDirectory(backupsDir);
            var old = DateTime.UtcNow.AddDays(-30);

            // Alphabetically first, so the sweep hits it before the two below.
            var keeper = Path.Combine(backupsDir, "palworld-20260101-120000-scheduled.zip");
            File.WriteAllText(keeper, "expired but read-only");
            File.SetLastWriteTimeUtc(keeper, old);
            File.SetAttributes(keeper, FileAttributes.ReadOnly);

            var expired = new List<string>();
            foreach (var stamp in new[] { "20260102-120000", "20260103-120000" })
            {
                var path = Path.Combine(backupsDir, $"palworld-{stamp}-scheduled.zip");
                File.WriteAllText(path, "expired and deletable");
                File.SetLastWriteTimeUtc(path, old);
                expired.Add(path);
            }

            var orphan = Path.Combine(backupsDir, "palworld-20260101-120000-shutdown.zip.partial");
            File.WriteAllText(orphan, "abandoned");
            File.SetLastWriteTimeUtc(orphan, old);

            var config = new LauncherConfig { ServerRoot = root, BackupRetentionDays = 7 };
            await new BackupService(config, new Logger(verbose: false))
                .BackupNowAsync(BackupReason.Manual, rest: null, serverRunning: false);

            foreach (var path in expired)
                Assert.False(File.Exists(path), $"{Path.GetFileName(path)} survived, the read-only file stopped the sweep");
            Assert.False(File.Exists(orphan), "the abandoned partial survived the sweep");
            Assert.True(File.Exists(keeper), "the read-only backup the user meant to keep was deleted");
        }
        finally
        {
            // Cleared before the recursive delete, otherwise cleanup throws and hides the real failure.
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_fresh_partial_and_a_compressible_world_both_survive_a_backup()
    {
        // Two false-positive guards at once: the grace period must spare a partial a live run is writing,
        // and the short-copy check compares uncompressed bytes, so highly compressible data must not trip it.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var backupsDir = BackupService.ResolveBackupsDir(root, null);
            Directory.CreateDirectory(backupsDir);
            var fresh = Path.Combine(backupsDir, "palworld-20260101-120000-scheduled.zip.partial");
            File.WriteAllText(fresh, "a run is writing this");

            var world = Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved", "SaveGames", "0", worldId);
            File.WriteAllBytes(Path.Combine(world, "Level.sav"), new byte[4 * 1024 * 1024]);

            var zipPath = await new BackupService(new LauncherConfig { ServerRoot = root }, new Logger(verbose: false))
                .BackupNowAsync(BackupReason.Manual, rest: null, serverRunning: false);

            Assert.NotNull(zipPath);
            Assert.True(File.Exists(fresh), "the sweep took a partial that was only minutes old");
            using var zip = ZipFile.OpenRead(zipPath!);
            var entry = zip.Entries.Single(e => e.FullName.EndsWith("Level.sav"));
            Assert.Equal(4 * 1024 * 1024, entry.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("palworld-20260101-120000-scheduled.zip", true)]
    [InlineData("palworld-٠١٢٣٤٥٦٧-٠١٢٣٤٥-scheduled.zip", false)]
    [InlineData("palworld-०१२३४५६७-०१२३४५-scheduled.zip", false)]
    public void Only_ascii_digits_count_as_our_naming(string fileName, bool expected) =>
        Assert.Equal(expected, BackupService.IsPrunableAutoBackup(fileName));



    [Fact]
    public void A_blocked_rename_reports_failure_and_leaves_no_backup_behind()
    {
        // A directory on the target name blocks the move the way a scanner's handle would. Reporting
        // success here would hand the user an archive under a name nothing manages.
        var dir = Path.Combine(Path.GetTempPath(), $"pal_promote_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var partial = Path.Combine(dir, "palworld-20260820-030000-scheduled.zip.partial");
            var wanted = Path.Combine(dir, "palworld-20260820-030000-scheduled.zip");
            File.WriteAllText(partial, "a complete archive");
            Directory.CreateDirectory(wanted);

            var service = new BackupService(new LauncherConfig { ServerRoot = dir }, new Logger(verbose: false));
            var landed = service.PromoteArchive(partial, wanted, BackupReason.Scheduled);

            Assert.Null(landed);
            // No backup under any name. The half-finished bytes go rather than sit somewhere unmanaged.
            Assert.False(File.Exists(partial));
            Assert.False(File.Exists(wanted + ".kept"));
            Assert.Empty(Directory.EnumerateFiles(dir, "*.zip"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task A_run_that_finds_its_partial_taken_leaves_that_file_alone()
    {
        // Another run owns a partial with this run's name. Whatever this run does, it must not touch bytes
        // it did not write. Every second the stamp could land on is pre-claimed so the collision is certain.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var backupsDir = BackupService.ResolveBackupsDir(root, null);
            Directory.CreateDirectory(backupsDir);
            var claimed = new List<string>();
            for (var second = 0; second <= 5; second++)
            {
                var stamp = DateTime.Now.AddSeconds(second).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var path = Path.Combine(backupsDir, $"palworld-{stamp}-scheduled.zip.partial");
                File.WriteAllText(path, "another run is writing this");
                claimed.Add(path);
            }

            var result = await new BackupService(new LauncherConfig { ServerRoot = root }, new Logger(verbose: false))
                .BackupNowAsync(BackupReason.Scheduled, rest: null, serverRunning: false);

            Assert.Null(result);
            foreach (var path in claimed)
            {
                Assert.True(File.Exists(path), $"{Path.GetFileName(path)} was deleted by a run that did not create it");
                Assert.Equal("another run is writing this", File.ReadAllText(path));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void A_name_already_taken_fails_without_burning_the_retries()
    {
        // Waiting cannot free a name another archive owns, and the advice for a held file is wrong here.
        var dir = Path.Combine(Path.GetTempPath(), $"pal_taken_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var lines = new List<string>();
        try
        {
            var partial = Path.Combine(dir, "palworld-20260820-030000-scheduled.zip.partial");
            var taken = Path.Combine(dir, "palworld-20260820-030000-scheduled.zip");
            File.WriteAllText(partial, "this run's archive");
            File.WriteAllText(taken, "an earlier archive");

            var logger = new Logger(verbose: false);
            logger.LineForUi += entry => lines.Add(entry.Text);
            var service = new BackupService(new LauncherConfig { ServerRoot = dir }, logger);

            var landed = service.PromoteArchive(partial, taken, BackupReason.Scheduled);

            Assert.Null(landed);
            Assert.Equal("an earlier archive", File.ReadAllText(taken));
            Assert.False(File.Exists(partial));
            Assert.Contains(lines, l => l.Contains("already exists"));
            Assert.DoesNotContain(lines, l => l.Contains("virus scanner"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task The_archive_runs_off_the_calling_thread()
    {
        // With no REST client nothing awaits before the zip, so the work used to run inline on whatever
        // thread called in. From the UI that is the dispatcher, frozen for the length of the archive.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var callingThread = Environment.CurrentManagedThreadId;
            var archivingThread = 0;
            var logger = new Logger(verbose: false);
            // Raised from inside the synchronous body, so it reports the thread doing the work.
            logger.LineForUi += _ => archivingThread = Environment.CurrentManagedThreadId;
            var service = new BackupService(new LauncherConfig { ServerRoot = root }, logger);

            var zipPath = await service.BackupNowAsync(BackupReason.Manual, rest: null, serverRunning: false);

            Assert.NotNull(zipPath);
            Assert.NotEqual(0, archivingThread);
            Assert.NotEqual(callingThread, archivingThread);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task A_save_that_shrinks_mid_archive_is_never_reported_as_a_backup()
    {
        // Palworld replaces Level.sav rather than rewriting it, so this guard is a tripwire for a writer
        // that truncates in place. What it must never do is call the short result a backup.
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var root = WriteFakeInstall(worldId);
        try
        {
            var world = Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved", "SaveGames", "0", worldId);
            var save = Path.Combine(world, "Level.sav");
            var bytes = new byte[96 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);            // incompressible, so the copy takes real time
            File.WriteAllBytes(save, bytes);

            var backupsDir = BackupService.ResolveBackupsDir(root, null);
            Directory.CreateDirectory(backupsDir);
            var service = new BackupService(new LauncherConfig { ServerRoot = root }, new Logger(verbose: false));

            using var truncated = new ManualResetEventSlim();
            var shrink = Task.Run(() =>
            {
                // Wait until the archive has grown well past the small files, which means the copy of
                // Level.sav itself is in flight. Waiting only for the partial to exist races its opening.
                // Bounded, because an archive that fails before Level.sav would otherwise hang the suite.
                var giveUp = DateTime.UtcNow.AddSeconds(10);
                while (DateTime.UtcNow < giveUp)
                {
                    var partial = Directory.EnumerateFiles(backupsDir, "*.partial").FirstOrDefault();
                    if (partial is not null && new FileInfo(partial).Exists && new FileInfo(partial).Length > 8 * 1024 * 1024)
                        break;
                    Thread.Sleep(1);
                }
                if (DateTime.UtcNow >= giveUp)
                    return;
                using var writer = new FileStream(save, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                writer.SetLength(64 * 1024);
                truncated.Set();
            });

            var zipPath = await service.BackupNowAsync(BackupReason.Scheduled, rest: null, serverRunning: false);
            await shrink;

            Assert.True(truncated.IsSet, "the archive never reached Level.sav, so nothing was truncated");
            Assert.Null(zipPath);
            Assert.Empty(Directory.EnumerateFiles(backupsDir, "*.zip"));
            Assert.Empty(Directory.EnumerateFiles(backupsDir, "*.partial"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void The_handle_and_the_path_report_different_times_after_a_swap()
    {
        // Why AddFile stamps entries from the handle. This pins the framework behavior it relies on, and
        // would not catch AddFile being changed back to the path overload.
        var dir = Path.Combine(Path.GetTempPath(), $"pal_stamp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var save = Path.Combine(dir, "Level.sav");
            var replacement = Path.Combine(dir, "Level.sav.new");
            File.WriteAllText(save, "the bytes we will read");
            File.WriteAllText(replacement, "the replacement");
            var original = new DateTime(2021, 3, 4, 5, 6, 7);
            File.SetLastWriteTime(save, original);
            File.SetLastWriteTime(replacement, new DateTime(2026, 8, 20, 1, 2, 3));

            using var source = BackupService.OpenForArchive(save);
            File.Delete(save);
            File.Move(replacement, save);

            Assert.Equal(original, File.GetLastWriteTime(source.SafeFileHandle));
            Assert.NotEqual(original, File.GetLastWriteTime(save));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

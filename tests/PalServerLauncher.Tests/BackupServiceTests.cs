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
}

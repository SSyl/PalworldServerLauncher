using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class BackupServiceTests
{
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

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;

namespace PalServerLauncher.Core;

/// <summary>
/// Snapshots the world + server config into a timestamped zip under <c>backups/</c>. Palworld only
/// writes the world on its own autosaves (never on shutdown/startup), so when the server is running
/// and REST is usable we <c>POST /save</c> first (synchronous, the file is on disk by the time it
/// returns) for a fresh archive; otherwise we archive whatever's on disk and warn it may be stale.
/// Palworld's own nested <c>backup/</c> dirs are excluded (they're redundant and dominate the size).
/// </summary>
public sealed class BackupService
{
    private readonly LauncherConfig _config;
    private readonly Logger _logger;

    public BackupService(LauncherConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    private string SavedDir => Path.Combine(_config.ServerRoot, LauncherConfig.ServerFolderName, "Pal", "Saved");
    private string SaveGamesDir => Path.Combine(SavedDir, "SaveGames");
    private string ConfigDir => Path.Combine(SavedDir, "Config", "WindowsServer");
    private string BackupsDir => Path.Combine(_config.ServerRoot, "backups");

    /// <summary>
    /// Take a backup. A fresh <c>/save</c> is issued only when the server is running AND REST is usable;
    /// otherwise the on-disk (last-autosave) state is archived with a staleness warning. Returns the
    /// zip path, or null if there was nothing to back up / it failed.
    /// </summary>
    public async Task<string?> BackupNowAsync(BackupReason reason, PalworldRestClient? rest, bool serverRunning, CancellationToken ct = default)
    {
        if (!Directory.Exists(SaveGamesDir))
        {
            _logger.Info($"Backup ({reason}) skipped, no save data yet.");
            return null;
        }

        if (serverRunning && rest is not null)
        {
            _logger.Info($"Backup ({reason}): saving world before archiving...");
            if (!await rest.SaveAsync(ct).ConfigureAwait(false))
                _logger.Info($"Backup ({reason}): /save was not accepted, archiving the current on-disk save, which may not include the latest changes.");
        }
        else
        {
            _logger.Info($"Backup ({reason}): no fresh save (REST off or server stopped), archiving the current on-disk save, which may not include the latest changes.");
        }

        Directory.CreateDirectory(BackupsDir);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var zipPath = Path.Combine(BackupsDir, $"palworld-{stamp}-{reason.ToString().ToLowerInvariant()}.zip");

        try
        {
            CreateArchive(zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Backup ({reason}) failed to write {Path.GetFileName(zipPath)}", ex);
            return null;
        }

        _logger.Info($"Backup ({reason}) written: {Path.GetFileName(zipPath)} ({new FileInfo(zipPath).Length / 1024d:F0} KB).");
        PruneOldBackups();
        return zipPath;
    }

    private void CreateArchive(string zipPath)
    {
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        AddTree(zip, SaveGamesDir, "SaveGames");
        if (Directory.Exists(ConfigDir))
            AddTree(zip, ConfigDir, "Config/WindowsServer");
    }

    private void AddTree(ZipArchive zip, string sourceDir, string entryRoot)
    {
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (ShouldSkipEntry(relative))
                continue;

            var entryName = $"{entryRoot}/{relative.Replace('\\', '/')}";
            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                entry.LastWriteTime = File.GetLastWriteTime(file);
                using var entryStream = entry.Open();
                // Share ReadWrite so a concurrent autosave writing the file doesn't fail the backup.
                using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                source.CopyTo(entryStream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Debug($"Backup: skipped locked/unreadable file {relative} ({ex.Message}).");
            }
        }
    }

    /// <summary>Skip anything inside a nested "backup" directory (Palworld's own rolling backups, bloat).</summary>
    public static bool ShouldSkipEntry(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/')
            .Any(segment => segment.Equals("backup", StringComparison.OrdinalIgnoreCase));

    // The archives THIS app writes: palworld-<yyyyMMdd>-<HHmmss>-<reason>.zip. The captured reason lets
    // retention skip MANUAL backups; any file that doesn't match (something a user dropped in) is left alone.
    private static readonly Regex OurBackupName = new(@"^palworld-\d{8}-\d{6}-([a-z]+)\.zip$", RegexOptions.IgnoreCase);

    /// <summary>
    /// True only for an auto-generated backup that retention is allowed to delete. Manual backups
    /// (user-made keepers) and any file not following our naming (a user's own drop-in) are never pruned.
    /// </summary>
    public static bool IsPrunableAutoBackup(string fileName)
    {
        var match = OurBackupName.Match(fileName);
        return match.Success
            && !match.Groups[1].Value.Equals(nameof(BackupReason.Manual), StringComparison.OrdinalIgnoreCase);
    }

    private void PruneOldBackups()
    {
        try
        {
            var files = Directory.GetFiles(BackupsDir, "palworld-*.zip")
                .Where(f => IsPrunableAutoBackup(Path.GetFileName(f)))
                .Select(f => (path: f, writtenUtc: File.GetLastWriteTimeUtc(f)));
            foreach (var expired in SelectExpired(files, _config.BackupRetentionDays, DateTime.UtcNow))
            {
                File.Delete(expired);
                _logger.Debug($"Pruned old backup: {Path.GetFileName(expired)}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"Backup prune skipped: {ex.Message}");
        }
    }

    /// <summary>Backups older than <paramref name="retentionDays"/> (0 or less = keep everything).</summary>
    public static IReadOnlyList<string> SelectExpired(
        IEnumerable<(string path, DateTime writtenUtc)> files, int retentionDays, DateTime nowUtc)
    {
        if (retentionDays <= 0)
            return Array.Empty<string>();

        var cutoff = nowUtc - TimeSpan.FromDays(retentionDays);
        return files.Where(f => f.writtenUtc < cutoff).Select(f => f.path).ToList();
    }
}

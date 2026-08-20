using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
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

    private string SavedDir => Path.Combine(LauncherConfig.ServerDir(_config.ServerRoot), "Pal", "Saved");
    private string SaveGamesDir => Path.Combine(SavedDir, "SaveGames");
    private string ConfigDir => Path.Combine(SavedDir, "Config", "WindowsServer");

    /// <summary>Where backup archives are written: the custom <see cref="LauncherConfig.BackupFolder"/> when set,
    /// otherwise the default <c>&lt;ServerRoot&gt;\backups</c>.</summary>
    private string BackupsDir => ResolveBackupsDir(_config.ServerRoot, _config.BackupFolder);

    /// <summary>Resolve the backup folder from the server root and the (possibly empty) custom override. A blank
    /// override falls back to <c>&lt;serverRoot&gt;\backups</c>. Pure so it's unit-tested directly.</summary>
    public static string ResolveBackupsDir(string serverRoot, string? backupFolder) =>
        string.IsNullOrWhiteSpace(backupFolder)
            ? Path.Combine(serverRoot, LauncherConfig.BackupsFolderName)
            : backupFolder;

    /// <summary>Verify a folder can actually be written to (the Save-time check for a custom path): create it if
    /// needed, write and delete a probe file. Returns false with a user-readable <paramref name="error"/> on any
    /// failure (bad path, missing drive, no permission), so the caller can warn instead of silently failing later.</summary>
    public static bool TryEnsureWritable(string dir, out string error)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".pslauncher-writetest.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            error = "";
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            error = Strings.BackupLoc_ErrorPermission;
            return false;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = Strings.BackupLoc_ErrorInvalid;
            return false;
        }
    }

    /// <summary>
    /// Take a backup. A fresh <c>/save</c> is issued only when the server is running AND REST is usable.
    /// A running server without REST archives the on-disk (last-autosave) state and warns that it may be
    /// stale. A stopped server has nothing live to be missing, so it archives without a warning. Returns
    /// the zip path, or null if there was nothing to back up / it failed.
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
        else if (serverRunning)
        {
            _logger.Info($"Backup ({reason}): REST is off, so the world can't be saved first. Archiving the current on-disk save, which may not include the latest changes.");
        }
        else
        {
            _logger.Info($"Backup ({reason}): archiving the current save.");
        }

        // Off the caller's thread. Nothing below awaits, so on the paths with no REST client (a manual
        // backup with the server stopped, and the startup backup) this ran on the UI thread and froze the
        // window for the length of the zip.
        return await Task.Run(() => Archive(reason), ct).ConfigureAwait(false);
    }

    /// <summary>The archive itself, all synchronous file work.</summary>
    private string? Archive(BackupReason reason)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        // Resolve the folder once so a config change mid-backup can't split the write and the prune across folders.
        var backupsDir = BackupsDir;
        var zipPath = Path.Combine(backupsDir, $"palworld-{stamp}-{reason.ToString().ToLowerInvariant()}.zip");
        // Built under a .partial name and renamed on success, so a half-written archive never sits under a
        // name a restore would offer. Nothing else in the file carries that guarantee.
        var partialPath = zipPath + PartialSuffix;
        int skipped;

        FileStream partial;
        try
        {
            // Inside the try: a custom backup folder could be invalid or unwritable (bad path, missing drive).
            Directory.CreateDirectory(backupsDir);
            // Two same-second runs compute the same partial name. CreateNew makes the loser throw here,
            // where the catch does NOT discard, so it can never delete the winner's file.
            partial = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            _logger.Error($"Backup ({reason}) couldn't start an archive in {backupsDir}", ex);
            return null;
        }

        try
        {
            skipped = CreateArchive(partial);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            _logger.Error($"Backup ({reason}) failed to write to {backupsDir}", ex);
            DiscardPartial(partialPath, reason);
            return null;
        }

        if (PromoteArchive(partialPath, zipPath, reason) is not { } finalPath)
            return null;
        zipPath = finalPath;

        var size = $"{new FileInfo(zipPath).Length / 1024d:F0} KB";
        _logger.Info(skipped == 0
            ? $"Backup ({reason}) written: {Path.GetFileName(zipPath)} ({size})."
            : $"Backup ({reason}) written: {Path.GetFileName(zipPath)} ({size}), {skipped} file(s) are NOT in it, see the log above.");
        PruneOldBackups(backupsDir);
        return zipPath;
    }

    /// <summary>
    /// Rename the finished archive into its real name, returning null when it never lands. A virus scan or
    /// a sync client can hold the file for a moment, hence the retries.
    /// </summary>
    internal string? PromoteArchive(string partialPath, string zipPath, BackupReason reason)
    {
        var held = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(partialPath, zipPath);
                // A measured hold, not a restatement of the attempt count. This is the only evidence anyone
                // has for whether PromoteAttempts is enough, so it has to be a real clock.
                if (attempt > 1)
                    _logger.Info($"Backup ({reason}): the rename to {Path.GetFileName(zipPath)} succeeded on try {attempt} after {held.ElapsedMilliseconds}ms.");
                return zipPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A name already taken cannot come free by waiting, so skip the retries. Two backups in the
                // same second, or the repeated hour on a DST fall-back, land here.
                if (File.Exists(zipPath))
                {
                    _logger.Error($"Backup ({reason}) could not be saved because {Path.GetFileName(zipPath)} already exists. " +
                        "Earlier backups are untouched, and the next scheduled one will take a fresh copy", ex);
                    DiscardPartial(partialPath, reason);
                    return null;
                }
                if (attempt >= PromoteAttempts)
                {
                    // Discarded rather than kept under a name of its own. A complete archive that nothing
                    // manages is a worse thing to hand a user than a failed backup.
                    _logger.Error($"Backup ({reason}) could not be renamed to {Path.GetFileName(zipPath)} after {held.ElapsedMilliseconds}ms, " +
                        "so it is not a backup. Something else is holding the file, often a virus scanner or a file sync " +
                        "client, and excluding the backups folder from it usually fixes this. Earlier backups are untouched", ex);
                    DiscardPartial(partialPath, reason);
                    return null;
                }
                Thread.Sleep(PromoteRetryMs);
            }
        }
    }

    // 750ms total. Both shutdown backups run immediately before rest.ShutdownAsync, so every millisecond
    // spent retrying delays a server shutdown. Raise it only against a measured hold, and raise the count,
    // not the shape.
    private const int PromoteAttempts = 4;
    private const int PromoteRetryMs = 250;

    /// <summary>Delete this run's half-written archive. Only ever the .partial file, never a finished one.</summary>
    private void DiscardPartial(string path, BackupReason reason)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.Info($"Backup ({reason}): discarded the incomplete {Path.GetFileName(path)}.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Backup ({reason}): couldn't remove the incomplete {Path.GetFileName(path)}", ex);
        }
    }

    /// <summary>Suffix an archive carries while it is being written. Chosen so the retention glob and
    /// <see cref="OurBackupName"/> both miss it, which keeps a half-written file from counting as a backup.</summary>
    private const string PartialSuffix = ".partial";

    /// <summary>The config files an archive carries. The rest of the folder (Engine.ini, Game.ini, ...) isn't part
    /// of a normal server's state, and where a user has customized one it's a rare, deliberate case.
    /// GameUserSettings.ini is here because its <c>DedicatedServerName</c> names the world folder the server loads:
    /// restore the saves without it into a fresh install and the server mints a new GUID and boots an empty world
    /// next to the restored one.</summary>
    public static readonly string[] ArchivedConfigFiles = ["PalWorldSettings.ini", "GameUserSettings.ini"];

    /// <summary>Writes the archive into an already-created file, returning how many files were left out.</summary>
    private int CreateArchive(FileStream destination)
    {
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create);
        var skipped = AddTree(zip, SaveGamesDir, "SaveGames");

        foreach (var name in ArchivedConfigFiles)
        {
            var path = Path.Combine(ConfigDir, name);
            if (!File.Exists(path))
            {
                // Silence here cost the world on restore: without GameUserSettings.ini the server mints a
                // new GUID and boots empty.
                _logger.Info($"Backup: {name} isn't there, so it isn't in this archive.");
                skipped++;
            }
            else if (!AddFile(zip, path, $"Config/WindowsServer/{name}"))
            {
                skipped++;
            }
        }
        return skipped;
    }

    private int AddTree(ZipArchive zip, string sourceDir, string entryRoot)
    {
        var skipped = 0;
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, file);
            if (ShouldSkipEntry(relative))
                continue;
            if (!AddFile(zip, file, $"{entryRoot}/{relative.Replace('\\', '/')}"))
                skipped++;
        }
        return skipped;
    }

    /// <summary>Copy one file in. False when it was left out, which the caller counts.</summary>
    private bool AddFile(ZipArchive zip, string file, string entryName)
    {
        FileStream source;
        try
        {
            // Opened before the entry exists. The other order leaves a zero-byte entry when the open fails,
            // and ZipArchiveMode.Create cannot delete one, so the archive ships an empty Level.sav.
            source = OpenForArchive(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Info($"Backup: left out {Path.GetFileName(file)}, it could not be opened ({ex.Message}).");
            return false;
        }

        using (source)
        {
            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
            // From the handle, so a save that swaps the file under us cannot stamp the entry with the
            // replacement's time. Zip timestamps run 1980-2107 and the setter throws outside that, which
            // would abandon the whole archive over a file whose mtime is merely odd.
            var modified = File.GetLastWriteTime(source.SafeFileHandle);
            if (modified.Year is >= 1980 and <= 2107)
                entry.LastWriteTime = modified;
            using var entryStream = entry.Open();
            var expected = source.Length;
            source.CopyTo(entryStream);
            // Truncated mid-copy, Read just returns 0 and CopyTo succeeds, so only the length catches it.
            // Failing the archive is deliberate: once the entry exists Create mode can't remove it, and a
            // short Level.sav inside a backup we call good is worse than no backup.
            if (entryStream.Position < expected)
                throw new IOException($"{Path.GetFileName(file)} shrank while it was being archived ({entryStream.Position} of {expected} bytes).");
        }
        return true;
    }

    /// <summary>
    /// Open one save file to copy into the archive. Measured: without FileShare.Delete a read handle blocks
    /// every way another process can swap that file (delete, delete-then-rename, ReplaceFile), and with it
    /// this handle still reads the pre-swap bytes. MoveFileEx with REPLACE_EXISTING stays blocked either way.
    /// </summary>
    public static FileStream OpenForArchive(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Skip anything inside a nested "backup" directory (Palworld's own rolling backups, bloat).</summary>
    public static bool ShouldSkipEntry(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/')
            .Any(segment => segment.Equals("backup", StringComparison.OrdinalIgnoreCase));

    // The archives THIS app writes: palworld-<yyyyMMdd>-<HHmmss>-<reason>.zip. The captured reason lets
    // retention skip MANUAL backups; any file that doesn't match (something a user dropped in) is left alone.
    // CultureInvariant: without it, IgnoreCase folds the 'i' in .zip under Turkish casing, so an uppercase
    // .ZIP stops matching and retention quietly keeps every such file forever. ASCII digit class for the
    // same reason, \d also accepts Arabic-Indic and Devanagari digits.
    private static readonly Regex OurBackupName =
        new(@"^palworld-[0-9]{8}-[0-9]{6}-([a-z]+)\.zip$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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

    private void PruneOldBackups(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "palworld-*.zip")
                .Where(f => IsPrunableAutoBackup(Path.GetFileName(f)))
                .Select(f => (path: f, writtenUtc: File.GetLastWriteTimeUtc(f)));
            // Per file: one undeletable backup (a user marks one read-only to keep it) must not stop the
            // rest of the sweep, which it did when a single throw left the whole prune unfinished.
            var stuck = SelectExpired(files, _config.BackupRetentionDays, DateTime.UtcNow)
                .Count(expired => !TryDelete(expired, "Pruned old backup"));
            // One line per run, not per file. A folder that permits create but not delete fails every
            // expired file every run, and per-file lines there evict the whole log buffer.
            if (stuck > 0)
                _logger.Info($"Backup: {stuck} old backup(s) couldn't be removed, so they still take up space.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"Backup prune skipped: {ex.Message}");
        }

        PruneStalePartials(dir);
    }

    /// <summary>Delete one file, reporting failure rather than throwing. False when it survived.</summary>
    private bool TryDelete(string path, string what)
    {
        try
        {
            File.Delete(path);
            _logger.Debug($"{what}: {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"Couldn't remove {Path.GetFileName(path)}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete .partial archives a killed run left behind. They match neither the retention glob nor
    /// <see cref="IsPrunableAutoBackup"/>, so nothing else ever reclaims that space. A partial a live run is
    /// writing is held open without FileShare.Delete, so the delete fails harmlessly, and the day of grace
    /// keeps this off a backup that is merely slow.
    /// </summary>
    private void PruneStalePartials(string dir)
    {
        try
        {
            var files = Directory.GetFiles(dir, "palworld-*" + PartialSuffix)
                .Where(f => IsOurPartialArchive(Path.GetFileName(f)))
                .Select(f => (path: f, writtenUtc: File.GetLastWriteTimeUtc(f)));
            foreach (var stale in SelectExpired(files, PartialGraceDays, DateTime.UtcNow))
                TryDelete(stale, "Removed an abandoned partial backup");

        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"Partial sweep skipped: {ex.Message}");
        }
    }

    /// <summary>Grace before an abandoned partial is swept, long enough for the slowest real archive.</summary>
    private const int PartialGraceDays = 1;

    /// <summary>
    /// True only for a partial THIS app wrote. The glob alone would also match a user's own
    /// palworld-notes.partial or a stalled browser download, and a custom backup folder can be any
    /// shared directory, so the name inside the suffix must still be one of ours.
    /// </summary>
    public static bool IsOurPartialArchive(string fileName) =>
        fileName.EndsWith(PartialSuffix, StringComparison.OrdinalIgnoreCase)
        && OurBackupName.IsMatch(fileName[..^PartialSuffix.Length]);

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

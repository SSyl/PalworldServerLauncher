using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// Manages Palworld's built-in Steam Workshop server mods. The server reads mods from its own
/// <c>Mods\Workshop</c> folder (the default, no <c>-workshopdir</c>) and enables them via
/// <c>Mods\PalModSettings.ini</c>. Two sources land in the same folder: mods the launcher downloads by
/// Workshop id (copied out of SteamCMD's workshop cache) and mods the user drops in themselves. Either
/// way the launcher reads each mod's <c>Info.json</c> for its PackageName, writes PalModSettings.ini, and
/// the server deploys them on its next (re)start. Constructed like <see cref="GameSettingsService"/>.
/// </summary>
public sealed class ModService
{
    private readonly string _serverRoot;
    private readonly Logger _logger;

    public ModService(string serverRoot, Logger logger)
    {
        _serverRoot = serverRoot;
        _logger = logger;
    }

    /// <summary>The server's Mods folder: <c>&lt;ServerRoot&gt;\PalworldDedicatedServer\Mods</c>.</summary>
    public string ModsDir => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName, "Mods");

    /// <summary>Where the server looks for Workshop mods by default (no -workshopdir): <c>Mods\Workshop</c>.</summary>
    public string WorkshopDir => Path.Combine(ModsDir, "Workshop");

    /// <summary>The mod enable/order file the server generates after its first run.</summary>
    public string PalModSettingsPath => Path.Combine(ModsDir, "PalModSettings.ini");

    /// <summary>One mod folder found under <c>Mods\Workshop</c>. <see cref="PackageName"/> is empty and
    /// <see cref="HasInfo"/> false when the folder has no readable Info.json yet.</summary>
    public sealed record InstalledMod(string FolderId, string PackageName, bool IsServer, bool HasInfo);

    /// <summary>
    /// Copy a freshly-downloaded Workshop item out of SteamCMD's cache (<paramref name="sourceDir"/>, from
    /// <see cref="SteamCmd.WorkshopContentDir"/>) into the server's <c>Mods\Workshop\&lt;id&gt;</c>, so the server
    /// finds it in its default mods folder. Overwrites an existing copy (a re-download is an update). Returns
    /// false if the source isn't there (the download didn't land).
    /// </summary>
    public bool CopyDownloadedMod(string workshopId, string sourceDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            _logger.Info($"Downloaded mod {workshopId} wasn't found at {sourceDir}.");
            return false;
        }
        CopyDirectory(sourceDir, Path.Combine(WorkshopDir, workshopId));
        _logger.Info($"Installed mod {workshopId} into Mods\\Workshop.");
        return true;
    }

    /// <summary>Enumerate the mod folders under <c>Mods\Workshop</c>, reading each one's Info.json. Powers both
    /// resolving a downloaded mod's PackageName and discovering dropped-in mods the user placed themselves.</summary>
    public IReadOnlyList<InstalledMod> ScanInstalledMods()
    {
        if (!Directory.Exists(WorkshopDir))
            return Array.Empty<InstalledMod>();

        var mods = new List<InstalledMod>();
        foreach (var dir in Directory.EnumerateDirectories(WorkshopDir))
        {
            var folderId = Path.GetFileName(dir);
            var info = ReadModInfo(dir);
            mods.Add(info is null
                ? new InstalledMod(folderId, "", IsServer: false, HasInfo: false)
                : new InstalledMod(folderId, info.PackageName, info.IsServer, HasInfo: true));
        }
        return mods;
    }

    /// <summary>Read the PackageName from a specific mod folder's Info.json (null if absent / unreadable).</summary>
    public string? ResolvePackageName(string workshopId) =>
        ReadModInfo(Path.Combine(WorkshopDir, workshopId))?.PackageName;

    /// <summary>True when PalModSettings.ini exists and currently has mods globally enabled. Lets the launcher
    /// avoid rewriting the file (or creating one on a never-modded install) just to leave it unchanged.</summary>
    public bool AreModsEnabledInIni()
    {
        if (!File.Exists(PalModSettingsPath))
            return false;
        try { return PalModSettingsFile.Load(File.ReadAllText(PalModSettingsPath)).GlobalEnable; }
        catch (IOException) { return false; }
    }

    /// <summary>
    /// Write <c>Mods\PalModSettings.ini</c>: set <c>bGlobalEnableMod</c> and the <c>ActiveModList</c> block to
    /// the given package names, preserving every other key (WorkshopRootDir / ConfigVersion / future keys). Loads
    /// the existing file, or starts an empty one the server will merge on next run. Safe whether stopped or
    /// running, the server only reads this on its next start.
    /// </summary>
    public void ApplyPalModSettings(bool globalEnable, IEnumerable<string> activePackageNames)
    {
        Directory.CreateDirectory(ModsDir);
        var existing = File.Exists(PalModSettingsPath) ? File.ReadAllText(PalModSettingsPath) : "";
        var file = PalModSettingsFile.Load(existing);
        file.SetGlobalEnable(globalEnable);
        file.SetActiveMods(activePackageNames);
        File.WriteAllText(PalModSettingsPath, file.Render());
        _logger.Info($"Wrote PalModSettings.ini (mods {(globalEnable ? "on" : "off")}, {file.ActiveMods.Count} active).");
    }

    /// <summary>Loose-paks folder: <c>Pal\Content\Paks\~mods</c>. Raw .pak mods (no Info.json) dropped here are
    /// mounted by the engine directly, outside the managed Workshop system.</summary>
    public string LoosePaksDir => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName, "Pal", "Content", "Paks", "~mods");

    /// <summary>Open the server's <c>Mods\Workshop</c> folder in Explorer (creating it first), for the
    /// "drop your own mods here" workflow.</summary>
    public void OpenModsFolder() => OpenFolder(WorkshopDir);

    /// <summary>Open the loose-paks folder (creating it first), for raw .pak mods that aren't Workshop-packaged.</summary>
    public void OpenLoosePaksFolder() => OpenFolder(LoosePaksDir);

    /// <summary>Delete a mod's source folder under <c>Mods\Workshop</c>. No-op if the name is blank or the folder
    /// is already gone. The server clears its own deployed copy on the next restart (the mod leaves ActiveModList).
    /// Exceptions propagate so the caller can report a failed delete.</summary>
    public void DeleteModFolder(string folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
            return;
        var dir = Path.Combine(WorkshopDir, folderName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.Info($"Deleted mod files: Mods\\Workshop\\{folderName}.");
        }
    }

    private void OpenFolder(string dir)
    {
        Directory.CreateDirectory(dir);
        try
        {
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.Info($"Couldn't open the folder: {ex.Message}");
        }
    }

    private static ModInfo? ReadModInfo(string modDir)
    {
        var infoPath = Path.Combine(modDir, "Info.json");
        if (!File.Exists(infoPath))
            return null;
        try { return ModInfo.Parse(File.ReadAllText(infoPath)); }
        catch (IOException) { return null; }
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destDir, Path.GetRelativePath(sourceDir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}

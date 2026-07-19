using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

    /// <summary>Where the server writes each deployed mod's <c>InstallManifest.json</c> after a restart:
    /// <c>Mods\ManagedMods\&lt;PackageName&gt;</c>. Deleting a mod's folder here makes the server redeploy it from
    /// the current source on its next start.</summary>
    public string ManagedModsDir => Path.Combine(ModsDir, "ManagedMods");

    /// <summary>The mod enable/order file the server generates after its first run.</summary>
    public string PalModSettingsPath => Path.Combine(ModsDir, "PalModSettings.ini");

    /// <summary>One mod folder found under <c>Mods\Workshop</c>. <see cref="PackageName"/> is empty and
    /// <see cref="HasInfo"/> false when the folder has no readable Info.json yet.</summary>
    public sealed record InstalledMod(string FolderId, string PackageName, bool IsServer, bool HasInfo);

    /// <summary>
    /// Mirror a freshly-downloaded Workshop item out of SteamCMD's cache (<paramref name="sourceDir"/>, from
    /// <see cref="SteamCmd.WorkshopContentDir"/>) into the server's <c>Mods\Workshop\&lt;id&gt;</c>, so the server
    /// finds it in its default mods folder. Wipes the destination first so it exactly matches the cache: an update
    /// that removed files leaves no stale ones behind, and any prior Force injection is cleanly replaced by the
    /// author's Info.json. Returns false if the source isn't there (the download didn't land).
    /// </summary>
    public bool CopyDownloadedMod(string workshopId, string sourceDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            _logger.Info($"Downloaded mod {workshopId} wasn't found at {sourceDir}.");
            return false;
        }
        var destDir = Path.Combine(WorkshopDir, workshopId);
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);
        CopyDirectory(sourceDir, destDir);
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

    /// <summary>Read the parsed Info.json for a mod folder under <c>Mods\Workshop</c> (its WorkshopId or scanned
    /// folder name), or null if it's absent/unreadable. Gives the caller PackageName + IsServer in one read, so
    /// the sync can decide ActiveModList membership from whether the mod declares server support.</summary>
    public ModInfo? GetModInfo(string folder) =>
        string.IsNullOrWhiteSpace(folder) ? null : ReadModInfo(Path.Combine(WorkshopDir, folder));

    /// <summary>
    /// Force a mod to deploy server-side by injecting <c>IsServer: true</c> into its source
    /// <c>Mods\Workshop\&lt;folder&gt;\Info.json</c> (see <see cref="ModInfoEditor"/> for the exact policy). Writes
    /// the file only when something actually changed. Returns the outcome so the caller can log it. A missing or
    /// unreadable/unwritable Info.json surfaces as <see cref="ForceOutcome.NotApplicable"/> so a bad file never
    /// blocks the launch.
    /// </summary>
    public ForceOutcome ForceServerFlag(string folder)
    {
        var infoPath = Path.Combine(WorkshopDir, folder, "Info.json");
        string json;
        try { json = File.ReadAllText(infoPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Info($"Couldn't read Info.json for mod '{folder}' to force it: {ex.Message}");
            return ForceOutcome.NotApplicable;
        }

        var result = ModInfoEditor.InjectServerFlag(json);
        if (result.Outcome == ForceOutcome.Forced)
        {
            try { File.WriteAllText(infoPath, result.Json!); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Info($"Couldn't write the forced Info.json for mod '{folder}': {ex.Message}");
                return ForceOutcome.NotApplicable;
            }
        }
        return result.Outcome;
    }

    /// <summary>Delete a mod's deployed manifest folder (<c>Mods\ManagedMods\&lt;PackageName&gt;</c>) so the server
    /// redeploys it from the current source on its next restart. No-op if the name is blank or the folder is gone.
    /// Exceptions propagate so the caller can log a failed clear.</summary>
    public void ClearDeployedMod(string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return;
        var dir = Path.Combine(ManagedModsDir, packageName);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            _logger.Info($"Cleared deployed manifest Mods\\ManagedMods\\{packageName} so the server redeploys it.");
        }
    }


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

    /// <summary>UE4SS script-mods folder: <c>Mods\NativeMods\UE4SS\Mods</c>, where UE4SS (installed via a Workshop
    /// mod) keeps its Lua mods. Only exists once a UE4SS mod has been deployed.</summary>
    public string Ue4ssModsDir => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName, "Mods", "NativeMods", "UE4SS", "Mods");

    /// <summary>True once UE4SS has been deployed (its mods folder exists).</summary>
    public bool Ue4ssInstalled => Directory.Exists(Ue4ssModsDir);

    /// <summary>Open the server's <c>Mods\Workshop</c> folder in Explorer (creating it first), for the
    /// "drop your own mods here" workflow.</summary>
    public void OpenModsFolder() => OpenFolder(WorkshopDir);

    /// <summary>Open the loose-paks folder (creating it first), for raw .pak mods that aren't Workshop-packaged.</summary>
    public void OpenLoosePaksFolder() => OpenFolder(LoosePaksDir);

    /// <summary>Open the UE4SS script-mods folder if UE4SS is installed. Doesn't create it (that would fake an
    /// install), so it's a no-op when UE4SS isn't there, the caller checks <see cref="Ue4ssInstalled"/>.</summary>
    public void OpenUe4ssModsFolder() => OpenFolder(Ue4ssModsDir, create: false);

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

    /// <summary>Scan the loose-paks folder (<c>~mods</c>) and group the files into mods. Empty when the folder
    /// doesn't exist yet.</summary>
    public IReadOnlyList<LoosePakMods.LoosePakMod> ScanLoosePaks()
    {
        if (!Directory.Exists(LoosePaksDir))
            return Array.Empty<LoosePakMods.LoosePakMod>();
        var names = Directory.EnumerateFiles(LoosePaksDir).Select(f => Path.GetFileName(f)!);
        return LoosePakMods.Scan(names);
    }

    /// <summary>Enable or disable a loose pak mod by renaming its files (never deleting them). Takes effect on the
    /// next server start. Exceptions propagate so the caller can report a failed rename.</summary>
    public void SetLoosePakEnabled(LoosePakMods.LoosePakMod mod, bool enable)
    {
        foreach (var (from, to) in LoosePakMods.TogglePlan(mod, enable))
        {
            var fromPath = Path.Combine(LoosePaksDir, from);
            if (File.Exists(fromPath))
                File.Move(fromPath, Path.Combine(LoosePaksDir, to), overwrite: true);
        }
        _logger.Info($"{(enable ? "Enabled" : "Disabled")} loose pak mod '{mod.BaseName}', takes effect on the next server start.");
    }

    private void OpenFolder(string dir, bool create = true)
    {
        if (create)
            Directory.CreateDirectory(dir);
        else if (!Directory.Exists(dir))
            return;
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

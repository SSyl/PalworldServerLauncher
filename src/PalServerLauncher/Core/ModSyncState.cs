using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PalServerLauncher.Core;

/// <summary>
/// Per-server record of which Workshop mod content the launcher last copied into <c>Mods\Workshop</c>, so it can
/// skip re-copying an unchanged mod on every start (the update-detection gate). Keyed by Workshop id to the
/// SteamCMD cache <c>manifest</c> last copied plus whether the Force-server injection was applied. Stored as its
/// own file at the server root (<c>ServerRoot</c>, alongside the server install) and written ONLY from the
/// mod-sync path (a single writer, serialized under the SteamCMD gate), so it never races
/// <see cref="Config.LauncherConfig.Save"/>. The
/// <see cref="NeedsSync"/> decision is pure, so it's unit-tested.
/// </summary>
public sealed class ModSyncState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public Dictionary<string, ModSyncEntry> Items { get; set; } = new();

    /// <summary>Where the state file lives for a given server root (next to launcher.json).</summary>
    public static string PathFor(string serverRoot) => Path.Combine(serverRoot, "mod-sync.json");

    public static ModSyncState Load(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ModSyncState>(File.ReadAllText(path)) ?? new ModSyncState();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A corrupt or unreadable state file just means we re-sync once, never fatal.
        }
        return new ModSyncState();
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort, same posture as LauncherConfig.Save. Losing it costs one redundant copy next start.
        }
    }

    /// <summary>Does this mod need a fresh copy from SteamCMD's cache into <c>Mods\Workshop</c>? True when its
    /// server folder is missing, we've never synced it, its cache content changed (<paramref name="liveManifest"/>
    /// differs), or its Force-server state changed since the last copy.</summary>
    public static bool NeedsSync(ModSyncEntry? recorded, string liveManifest, bool forced, bool folderPresent) =>
        !folderPresent
        || recorded is null
        || recorded.Manifest != liveManifest
        || recorded.Forced != forced;
}

/// <summary>The last-synced signature of one Workshop mod: the cache manifest copied and the force state applied.</summary>
public sealed class ModSyncEntry
{
    public string Manifest { get; set; } = "";
    public bool Forced { get; set; }
}

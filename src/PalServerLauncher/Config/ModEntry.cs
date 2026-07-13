namespace PalServerLauncher.Config;

/// <summary>
/// One managed mod. Either a Steam Workshop item (<see cref="WorkshopId"/> set, downloaded by the launcher)
/// or a local/dropped-in mod (<see cref="WorkshopId"/> empty, the user placed it in Mods/Workshop themselves).
/// Persisted in launcher.json so the list, notes, and resolved package names survive across sessions.
/// </summary>
public sealed class ModEntry
{
    /// <summary>The Steam Workshop id (digits), or empty for a local/dropped-in mod.</summary>
    public string WorkshopId { get; set; } = "";

    /// <summary>Whether this mod is enabled (written to PalModSettings.ini's ActiveModList on the next start).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Display name, auto-filled from Steam on add, user-editable.</summary>
    public string ModName { get; set; } = "";

    /// <summary>Optional free-text note (what the mod is, why). Not required.</summary>
    public string Note { get; set; } = "";

    /// <summary>The mod's PackageName from its Info.json, resolved after download/scan. This is the value
    /// PalModSettings.ini's ActiveModList needs (NOT the folder name or the Workshop id).</summary>
    public string PackageName { get; set; } = "";

    /// <summary>The mod's folder name under Mods\Workshop. For a downloaded mod this is the WorkshopId; for a
    /// dropped-in (local) mod it's the folder the Scan found. Lets Remove delete the right folder.</summary>
    public string FolderName { get; set; } = "";

    /// <summary>Steam's last-updated timestamp for the Workshop item, for an "update available" hint.</summary>
    public long TimeUpdated { get; set; }

    /// <summary>A field-for-field copy, so the Mods dialog can edit a working copy and discard it on Cancel.</summary>
    public ModEntry Clone() => new()
    {
        WorkshopId = WorkshopId,
        Enabled = Enabled,
        ModName = ModName,
        Note = Note,
        PackageName = PackageName,
        FolderName = FolderName,
        TimeUpdated = TimeUpdated,
    };
}

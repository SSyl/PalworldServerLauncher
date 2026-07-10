using System.Collections.Generic;
using System.IO;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// SCAFFOLDING ONLY - not implemented. Placeholder for managing server-side mods.
///
/// IMPORTANT UNCERTAINTY (surfaced for the owner): Palworld dedicated-server modding is NOT Steam
/// Workshop. Workshop items are client-side and there's no dedicated-server Workshop pipeline. Server
/// mods in practice are:
///   - **UE4SS** (Lua/C++ runtime injected into the server) + individual mods placed under
///     `Pal/Binaries/Win64/` (e.g. `ue4ss/Mods/`), and/or
///   - **PalDefender** (a popular admin/anti-cheat mod loader), also UE4SS-based.
/// NexusMods is a distribution site for these; it is not an install mechanism.
///
/// So "Steam Workshop + NexusMods" needs the owner to confirm the intended flow before building. A
/// realistic first implementation is: detect/install UE4SS, enumerate mods in its Mods folder, and
/// enable/disable them via UE4SS's `mods.txt` - all while the server is stopped. This class is the
/// hook point for that; the config carries <see cref="LauncherConfig.ModsEnabled"/> +
/// <see cref="LauncherConfig.EnabledMods"/> placeholders.
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

    /// <summary>Where UE4SS + its mods would live (best guess; confirm against the actual UE4SS layout).</summary>
    public string ModsRoot => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName, "Pal", "Binaries", "Win64", "ue4ss", "Mods");

    /// <summary>True if a UE4SS mods folder is present (a proxy for "modding is set up").</summary>
    public bool IsModLoaderInstalled => Directory.Exists(ModsRoot);

    /// <summary>Placeholder: list installed mods. Not implemented until the mechanism is confirmed.</summary>
    public IReadOnlyList<string> ListInstalledMods() => System.Array.Empty<string>();

    // TODO(owner-confirm): install UE4SS/PalDefender; enable/disable mods via ue4ss/Mods/mods.txt
    // (server-stopped only); optionally fetch from NexusMods by URL. See class remarks.
}

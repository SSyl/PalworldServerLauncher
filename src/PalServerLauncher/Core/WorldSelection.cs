using System.Collections.Generic;
using System.Linq;

namespace PalServerLauncher.Core;

/// <summary>What the world folders on disk say about the world the server is configured to load.</summary>
public enum WorldSelectionState
{
    /// <summary>The configured world exists. Nothing to do.</summary>
    Ok,

    /// <summary>No saves yet (a fresh install, or the server has never finished a first boot).</summary>
    NoWorlds,

    /// <summary>The configured world is missing or unset and exactly one save exists, so the fix is unambiguous.</summary>
    Recoverable,

    /// <summary>The configured world is missing or unset and several saves exist, so only the user can say which.</summary>
    Ambiguous,
}

public readonly record struct WorldSelectionVerdict(WorldSelectionState State, string? WorldId);

/// <summary>
/// Decides whether <c>DedicatedServerName</c> still names a world that exists on disk. It stops the server from
/// silently creating an empty world beside a save it was never pointed at, which is what happens after a restore,
/// an import, or any hand-copied save folder. Pure, so it's unit-tested.
/// </summary>
public static class WorldSelection
{
    public static WorldSelectionVerdict Evaluate(string? configuredWorldId, IReadOnlyList<string> worldIds)
    {
        if (worldIds.Count == 0)
            return new WorldSelectionVerdict(WorldSelectionState.NoWorlds, null);

        var configured = configuredWorldId?.Trim();
        if (!string.IsNullOrEmpty(configured) &&
            worldIds.Any(id => string.Equals(id, configured, StringComparison.OrdinalIgnoreCase)))
            return new WorldSelectionVerdict(WorldSelectionState.Ok, configured);

        return worldIds.Count == 1
            ? new WorldSelectionVerdict(WorldSelectionState.Recoverable, worldIds[0])
            : new WorldSelectionVerdict(WorldSelectionState.Ambiguous, null);
    }
}

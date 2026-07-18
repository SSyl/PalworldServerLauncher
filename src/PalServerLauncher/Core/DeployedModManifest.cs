using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace PalServerLauncher.Core;

/// <summary>
/// Reads the server's own <c>Mods\ManagedMods\&lt;PackageName&gt;\InstallManifest.json</c> (its record of every
/// file and dir it deployed for a mod) to pick exactly what to remove when un-forcing that mod. The Palworld
/// server does NOT clean up a mod's deployed files on its own, even on a Version change (verified on a real
/// server), so un-force reverses the deployment the launcher caused, using the server's own record.
///
/// Everything selected must carry the PackageName as a full path segment, so a forced Lua mod's uninstall can
/// only ever touch <c>.../Mods/&lt;pkg&gt;/...</c> and <c>ManagedMods/&lt;pkg&gt;</c> - never UE4SS itself,
/// shared helpers, or any path outside the mod. Pure, so it's unit-tested.
/// </summary>
public static class DeployedModManifest
{
    /// <summary>The server-folder-relative files and dirs to delete to reverse a mod's deployment.</summary>
    public sealed record UninstallPlan(IReadOnlyList<string> Files, IReadOnlyList<string> Dirs);

    /// <summary>Pick the Files and Dirs from an InstallManifest.json that belong to <paramref name="packageName"/>
    /// (carry it as a full path segment). Empty on parse failure or a blank package name.</summary>
    public static UninstallPlan Select(string manifestJson, string packageName)
    {
        if (string.IsNullOrWhiteSpace(packageName))
            return new UninstallPlan(Array.Empty<string>(), Array.Empty<string>());
        try
        {
            using var doc = JsonDocument.Parse(manifestJson);
            var files = ReadStringArray(doc.RootElement, "Files").Where(p => HasSegment(p, packageName)).ToList();
            var dirs = ReadStringArray(doc.RootElement, "Dirs").Where(p => HasSegment(p, packageName)).ToList();
            return new UninstallPlan(files, dirs);
        }
        catch (JsonException)
        {
            return new UninstallPlan(Array.Empty<string>(), Array.Empty<string>());
        }
    }

    /// <summary>True when <paramref name="packageName"/> is a full path segment of <paramref name="path"/>, so
    /// "SmartTransport" matches ".../Mods/SmartTransport/Scripts" but NOT ".../Mods/shared" or ".../UE4SS".</summary>
    public static bool HasSegment(string path, string packageName) =>
        path.Split('/', '\\').Any(seg => seg.Equals(packageName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> ReadStringArray(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : Enumerable.Empty<string>();
}

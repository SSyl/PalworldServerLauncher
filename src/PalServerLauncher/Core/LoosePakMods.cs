using System;
using System.Collections.Generic;
using System.Linq;

namespace PalServerLauncher.Core;

/// <summary>
/// Pure grouping and toggle logic for loose .pak mods, the raw paks a user drops into
/// <c>Pal\Content\Paks\~mods</c> outside the managed Workshop system. A UE5 pak mod is a set of files sharing a
/// base name: <c>Name.pak</c> plus, for IoStore mods, <c>Name.utoc</c> and <c>Name.ucas</c>. It's disabled by
/// renaming every file with a <c>.disabled</c> suffix (the engine only mounts <c>.pak</c>/<c>.utoc</c>) and
/// re-enabled by stripping it, so nothing is ever deleted. This does the grouping and the rename plan, the file
/// I/O lives in <see cref="ModService"/>. Pure, so it's unit-tested.
/// </summary>
public static class LoosePakMods
{
    public const string DisabledSuffix = ".disabled";
    private static readonly string[] PakExtensions = { ".pak", ".utoc", ".ucas" };

    /// <summary>One loose mod: a base name, whether it's currently enabled, and its files as they appear on disk.</summary>
    public sealed record LoosePakMod(string BaseName, bool Enabled, IReadOnlyList<string> Files);

    /// <summary>Group a folder's file names into loose mods, ignoring non-pak files. A mod is enabled when at
    /// least one of its files is not disabled.</summary>
    public static IReadOnlyList<LoosePakMod> Scan(IEnumerable<string> fileNames)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in fileNames)
        {
            if (!IsPakFamily(name))
                continue;
            var baseName = BaseNameOf(name);
            if (!groups.TryGetValue(baseName, out var list))
                groups[baseName] = list = new List<string>();
            list.Add(name);
        }
        return groups
            .Select(g => new LoosePakMod(g.Key, g.Value.Any(f => !IsDisabled(f)), g.Value))
            .OrderBy(m => m.BaseName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The (from -> to) renames to enable or disable a mod. Only files that actually change are returned.</summary>
    public static IReadOnlyList<(string From, string To)> TogglePlan(LoosePakMod mod, bool enable)
    {
        var plan = new List<(string, string)>();
        foreach (var file in mod.Files)
        {
            var target = enable ? StripDisabled(file) : AddDisabled(file);
            if (!string.Equals(file, target, StringComparison.Ordinal))
                plan.Add((file, target));
        }
        return plan;
    }

    /// <summary>True for a pak-family file (.pak/.utoc/.ucas), enabled or disabled.</summary>
    public static bool IsPakFamily(string fileName)
    {
        var core = StripDisabled(fileName);
        return PakExtensions.Any(ext => core.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsDisabled(string fileName) =>
        fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);

    private static string AddDisabled(string fileName) =>
        IsDisabled(fileName) ? fileName : fileName + DisabledSuffix;

    private static string StripDisabled(string fileName) =>
        IsDisabled(fileName) ? fileName[..^DisabledSuffix.Length] : fileName;

    /// <summary>The base name a mod's files share: strip a trailing .disabled, then the pak-family extension.</summary>
    private static string BaseNameOf(string fileName)
    {
        var core = StripDisabled(fileName);
        foreach (var ext in PakExtensions)
            if (core.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return core[..^ext.Length];
        return core;
    }
}

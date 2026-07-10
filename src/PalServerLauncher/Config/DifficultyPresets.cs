using System.Collections.Generic;
using System.Linq;

namespace PalServerLauncher.Config;

/// <summary>
/// Launcher difficulty presets: one click sets a coherent group of PalWorldSettings.ini keys. The explicit
/// values are a Palworld 1.0 snapshot. Every preset covers the same <see cref="Keys"/> set: it applies its
/// listed values and reverts the rest of the set to the live server defaults, so switching from (say)
/// Hardcore to Casual can't leave the hardcore flags behind. "Normal" lists nothing, so it reverts all of
/// <see cref="Keys"/> to the defaults. The game's own Difficulty key is intentionally left alone (its default
/// None is what lets these individual settings take effect). Applied through the normal stopped-only,
/// round-trip-verified save path.
/// </summary>
public static class DifficultyPresets
{
    public static readonly IReadOnlyList<string> Names = new[] { "Casual", "Normal", "Hard", "Hardcore" };

    /// <summary>The full set of keys any preset touches. Unlisted keys (and all of them for Normal) revert to
    /// the live server defaults.</summary>
    public static readonly IReadOnlyList<string> Keys = new[]
    {
        "ExpRate", "PalCaptureRate", "PlayerDamageRateAttack", "PlayerDamageRateDefense",
        "CollectionDropRate", "EnemyDropItemRate", "PalEggDefaultHatchingTime", "DeathPenalty",
        "bHardcore", "bPalLost", "bAllowGlobalPalboxExport", "bAllowGlobalPalboxImport",
    };

    // Explicit per-preset values (Palworld 1.0). Floats are given plainly and formatted on write; DeathPenalty
    // uses the bare enum token. Normal is empty (everything reverts to the server default).
    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Presets =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Casual"] = new Dictionary<string, string>
            {
                ["ExpRate"] = "1.3",
                ["PalCaptureRate"] = "2",
                ["PlayerDamageRateAttack"] = "1.5",
                ["PlayerDamageRateDefense"] = "0.7",
                ["CollectionDropRate"] = "2",
                ["EnemyDropItemRate"] = "2",
                ["PalEggDefaultHatchingTime"] = "0",
                ["DeathPenalty"] = "None",
            },
            ["Normal"] = new Dictionary<string, string>(),
            ["Hard"] = new Dictionary<string, string>
            {
                ["ExpRate"] = "0.8",
                ["PalCaptureRate"] = "0.8",
                ["PlayerDamageRateAttack"] = "0.5",
                ["PlayerDamageRateDefense"] = "1.5",
                ["CollectionDropRate"] = "0.5",
                ["EnemyDropItemRate"] = "0.5",
                ["PalEggDefaultHatchingTime"] = "2",
                ["DeathPenalty"] = "All",
            },
            ["Hardcore"] = new Dictionary<string, string>
            {
                ["ExpRate"] = "0.8",
                ["PalCaptureRate"] = "0.8",
                ["PlayerDamageRateAttack"] = "0.5",
                ["PlayerDamageRateDefense"] = "2.0",
                ["PalEggDefaultHatchingTime"] = "2",
                ["DeathPenalty"] = "All",
                ["bHardcore"] = "True",
                ["bPalLost"] = "True",
                ["bAllowGlobalPalboxExport"] = "False",
                ["bAllowGlobalPalboxImport"] = "False",
            },
        };

    /// <summary>
    /// The (key, target-value) changes a preset would make, given the live server defaults and the current
    /// values. A key is included only when its target differs from the current value (typed comparison, so
    /// 1.0 vs 1.000000 isn't flagged). Target = the preset's explicit value, or the server default when the
    /// preset doesn't list it. Unknown preset name = no changes.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> ResolveChanges(
        string presetName,
        IReadOnlyDictionary<string, string?> serverDefaults,
        IReadOnlyDictionary<string, string?> current)
    {
        if (!Presets.TryGetValue(presetName, out var explicitValues))
            return System.Array.Empty<(string, string)>();

        var byKey = GameSettingsCatalog.All.ToDictionary(s => s.Key, StringComparer.OrdinalIgnoreCase);
        var changes = new List<(string, string)>();
        foreach (var key in Keys)
        {
            var target = explicitValues.TryGetValue(key, out var pv)
                ? pv
                : serverDefaults.TryGetValue(key, out var dv) ? dv ?? "" : "";
            var cur = current.TryGetValue(key, out var cv) ? cv ?? "" : "";
            var type = byKey.TryGetValue(key, out var setting) ? setting.Type : SettingType.Text;
            if (!SettingValidator.ValuesEqual(type, cur, target))
                changes.Add((key, target));
        }
        return changes;
    }
}

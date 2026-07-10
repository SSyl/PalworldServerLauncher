using System.Collections.Generic;
using System.Linq;
using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class DifficultyPresetsTests
{
    // The 1.0 server defaults for the keys presets touch.
    private static Dictionary<string, string?> Defaults() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ExpRate"] = "1.000000",
        ["PalCaptureRate"] = "1.000000",
        ["PlayerDamageRateAttack"] = "1.000000",
        ["PlayerDamageRateDefense"] = "1.000000",
        ["CollectionDropRate"] = "1.000000",
        ["EnemyDropItemRate"] = "1.000000",
        ["PalEggDefaultHatchingTime"] = "1.000000",
        ["DeathPenalty"] = "Item",
        ["bHardcore"] = "False",
        ["bPalLost"] = "False",
        ["bAllowGlobalPalboxExport"] = "True",
        ["bAllowGlobalPalboxImport"] = "False",
    };

    private static Dictionary<string, string> AsMap(IReadOnlyList<(string Key, string Value)> changes) =>
        changes.ToDictionary(c => c.Key, c => c.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Casual_from_defaults_changes_only_its_listed_keys()
    {
        var changes = DifficultyPresets.ResolveChanges("Casual", Defaults(), Defaults());
        var map = AsMap(changes);

        // The 8 keys Casual lists differ from the default; the 4 it doesn't list are already at default.
        Assert.Equal(8, changes.Count);
        Assert.Equal("1.3", map["ExpRate"]);
        Assert.Equal("2", map["PalCaptureRate"]);
        Assert.Equal("0.7", map["PlayerDamageRateDefense"]);
        Assert.Equal("0", map["PalEggDefaultHatchingTime"]);
        Assert.Equal("None", map["DeathPenalty"]);
        Assert.DoesNotContain("bHardcore", map.Keys);
    }

    [Fact]
    public void Normal_reverts_a_hardcore_config_back_to_defaults()
    {
        var current = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
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
            ["bAllowGlobalPalboxImport"] = "False", // already at the default
            ["CollectionDropRate"] = "1.000000",     // Hardcore left these at default
            ["EnemyDropItemRate"] = "1.000000",
        };

        var map = AsMap(DifficultyPresets.ResolveChanges("Normal", Defaults(), current));

        Assert.Equal("Item", map["DeathPenalty"]);
        Assert.Equal("False", map["bHardcore"]);
        Assert.Equal("True", map["bAllowGlobalPalboxExport"]);
        // Values already at the default aren't reported as changes.
        Assert.DoesNotContain("bAllowGlobalPalboxImport", map.Keys);
        Assert.DoesNotContain("CollectionDropRate", map.Keys);
    }

    [Fact]
    public void A_preset_that_matches_current_produces_no_changes()
    {
        var defaults = Defaults();
        // Current == exactly what Casual would set (its values, defaults for the rest).
        var casual = AsMap(DifficultyPresets.ResolveChanges("Casual", defaults, defaults));
        var current = new Dictionary<string, string?>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in casual)
            current[key] = value;

        Assert.Empty(DifficultyPresets.ResolveChanges("Casual", defaults, current));
    }

    [Fact]
    public void Float_formatting_differences_are_not_flagged_as_changes()
    {
        // Current already Casual, but ExpRate written in the game's 6-decimal form.
        var defaults = Defaults();
        var current = new Dictionary<string, string?>(defaults, StringComparer.OrdinalIgnoreCase)
        {
            ["ExpRate"] = "1.300000",
        };
        var map = AsMap(DifficultyPresets.ResolveChanges("Casual", defaults, current));
        Assert.DoesNotContain("ExpRate", map.Keys); // 1.3 vs 1.300000 is the same value
    }
}

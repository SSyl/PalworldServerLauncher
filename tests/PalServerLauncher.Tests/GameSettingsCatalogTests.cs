using System;
using System.IO;
using System.Linq;
using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class GameSettingsCatalogTests
{
    // A verbatim copy of a real Palworld 1.0 DefaultPalWorldSettings.ini (119 keys), copied to the test
    // output. It's the source of truth for the coverage guard below: refresh it from a new default ini when
    // the game version changes, and update the catalog until this passes again.
    private static string Load10DefaultIni() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "DefaultPalWorldSettings-1.0.ini"));

    [Fact]
    public void Catalog_covers_every_key_in_a_fresh_1_0_default_ini()
    {
        // Guards the promise that the Undocumented tab's "new in your config" section stays empty on a normal
        // 1.0 install: a game update that adds a key we haven't catalogued (or a catalog key that drifts from
        // the default ini) fails here instead of quietly surfacing as an unrecognised setting in the UI.
        var blob = OptionSettingsBlob.Load(Load10DefaultIni());
        Assert.True(blob.HasOptionSettings);

        var catalogKeys = GameSettingsCatalog.All.Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = blob.Keys.Where(k => !catalogKeys.Contains(k)).ToList();

        Assert.True(missing.Count == 0, $"Catalog is missing 1.0 default keys: {string.Join(", ", missing)}");
    }

    [Fact]
    public void Catalog_has_no_duplicate_keys()
    {
        var dupes = GameSettingsCatalog.All
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.True(dupes.Count == 0, $"Duplicate catalog keys: {string.Join(", ", dupes)}");
    }

    [Fact]
    public void Enum_settings_all_declare_their_options()
    {
        var badEnums = GameSettingsCatalog.All
            .Where(s => s.Type == SettingType.Enum && (s.Options is null || s.Options.Count == 0))
            .Select(s => s.Key)
            .ToList();
        Assert.True(badEnums.Count == 0, $"Enum settings without options: {string.Join(", ", badEnums)}");
    }
}

using System.Collections.Generic;
using System.Globalization;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;

namespace PalServerLauncher.Tests;

public class CatalogTextTests
{
    // CatalogText prefers the Cat_<Key>_Label / Cat_<Key>_Desc resx value over the inline catalog English,
    // falling back to the inline string only when the resx value is missing or empty. So wherever BOTH exist,
    // they must stay identical, otherwise the resx silently shadows a diverged inline string and the inline
    // English becomes dead, misleading text. This pins them equal (they were seeded equal during localization).
    [Fact]
    public void Inline_catalog_english_matches_the_resx_english()
    {
        var mismatches = new List<string>();
        foreach (var setting in GameSettingsCatalog.All)
        {
            var resxLabel = Strings.ResourceManager.GetString($"Cat_{setting.Key}_Label", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(resxLabel) && resxLabel != setting.Label)
                mismatches.Add($"{setting.Key} Label: catalog=\"{setting.Label}\" resx=\"{resxLabel}\"");

            var resxDesc = Strings.ResourceManager.GetString($"Cat_{setting.Key}_Desc", CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(resxDesc) && resxDesc != setting.Description)
                mismatches.Add($"{setting.Key} Desc: catalog=\"{setting.Description}\" resx=\"{resxDesc}\"");
        }

        Assert.True(mismatches.Count == 0,
            "Inline catalog English drifted from the Cat_* resx English (the resx wins the lookup, so the inline is dead / misleading):\n"
            + string.Join("\n", mismatches));
    }
}

using System.Globalization;
using PalServerLauncher.Localization;

namespace PalServerLauncher.Tests;

// Proof-of-concept coverage for GitHub issue #1 (Chinese translation). Strings.resx/Strings.zh-Hans.resx
// currently hold a handful of placeholder keys, not a real extraction, this just proves the resx + MSBuild
// strongly-typed-class mechanism actually resolves both languages before the real feature is built on top of it.
public class LocalizationSmokeTests
{
    [Fact]
    public void English_is_the_default_culture()
    {
        Strings.Culture = CultureInfo.InvariantCulture;
        Assert.Equal("Start", Strings.Start_Button);
    }

    [Fact]
    public void Chinese_satellite_resolves_via_explicit_culture()
    {
        var zhHans = CultureInfo.GetCultureInfo("zh-Hans");
        Assert.Equal("启动", Strings.ResourceManager.GetString("Start_Button", zhHans));
    }

    [Fact]
    public void Chinese_satellite_has_its_own_resource_set_not_a_fallback_to_english()
    {
        var zhHans = CultureInfo.GetCultureInfo("zh-Hans");
        var resourceSet = Strings.ResourceManager.GetResourceSet(zhHans, createIfNotExists: true, tryParents: false);
        Assert.NotNull(resourceSet);
        Assert.Equal("请重启启动器以应用{0}。", resourceSet!.GetString("RestartNotice_Format"));
    }

    [Fact]
    public void English_and_Chinese_resource_sets_have_the_same_keys()
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        var zhHans = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("zh-Hans"), createIfNotExists: true, tryParents: false);
        Assert.NotNull(english);
        Assert.NotNull(zhHans);

        var englishKeys = english!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        var zhHansKeys = zhHans!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();

        var missingFromChinese = englishKeys.Except(zhHansKeys).ToList();
        var missingFromEnglish = zhHansKeys.Except(englishKeys).ToList();

        Assert.True(missingFromChinese.Count == 0, $"Keys missing from Strings.zh-Hans.resx: {string.Join(", ", missingFromChinese)}");
        Assert.True(missingFromEnglish.Count == 0, $"Keys missing from Strings.resx: {string.Join(", ", missingFromEnglish)}");
    }
}

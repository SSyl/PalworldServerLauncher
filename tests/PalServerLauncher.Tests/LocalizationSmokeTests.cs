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
        Assert.Equal("立即重启", resourceSet!.GetString("LauncherSettings_RestartNow"));
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

    [Fact]
    public void English_and_TraditionalChinese_resource_sets_have_the_same_keys()
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        var zhHant = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("zh-Hant"), createIfNotExists: true, tryParents: false);
        Assert.NotNull(english);
        Assert.NotNull(zhHant);

        var englishKeys = english!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        var zhHantKeys = zhHant!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();

        var missingFromHant = englishKeys.Except(zhHantKeys).ToList();
        var missingFromEnglish = zhHantKeys.Except(englishKeys).ToList();

        Assert.True(missingFromHant.Count == 0, $"Keys missing from Strings.zh-Hant.resx: {string.Join(", ", missingFromHant)}");
        Assert.True(missingFromEnglish.Count == 0, $"Keys missing from Strings.resx: {string.Join(", ", missingFromEnglish)}");
    }

    [Fact]
    public void English_and_Japanese_resource_sets_have_the_same_keys()
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        var ja = Strings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("ja"), createIfNotExists: true, tryParents: false);
        Assert.NotNull(english);
        Assert.NotNull(ja);

        var englishKeys = english!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();
        var jaKeys = ja!.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).ToHashSet();

        var missingFromJapanese = englishKeys.Except(jaKeys).ToList();
        var missingFromEnglish = jaKeys.Except(englishKeys).ToList();

        Assert.True(missingFromJapanese.Count == 0, $"Keys missing from Strings.ja.resx: {string.Join(", ", missingFromJapanese)}");
        Assert.True(missingFromEnglish.Count == 0, $"Keys missing from Strings.resx: {string.Join(", ", missingFromEnglish)}");
    }

    // A translated string.Format template that uses a different set of {N} placeholders than the English one
    // throws FormatException at runtime. Several of these format calls run in background loops (uptime display,
    // schedulers) whose only catch is OperationCanceledException, so a bad translation would silently kill
    // health monitoring / scheduling. Key parity alone does not catch this, so assert placeholder parity too.
    [Theory]
    [InlineData("zh-Hans")]
    [InlineData("zh-Hant")]
    [InlineData("ja")]
    public void Format_placeholders_match_english_in_every_satellite(string cultureName)
    {
        var english = Strings.ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(english);
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var mismatches = new List<string>();
        foreach (System.Collections.DictionaryEntry entry in english!)
        {
            var key = (string)entry.Key;
            var enIndices = PlaceholderIndices((string)entry.Value!);
            if (enIndices.Count == 0)
                continue; // not a format string, nothing to check

            var translated = Strings.ResourceManager.GetString(key, culture);
            var translatedIndices = translated is null ? new HashSet<int>() : PlaceholderIndices(translated);
            if (!enIndices.SetEquals(translatedIndices))
                mismatches.Add($"{key}: en={{{string.Join(",", enIndices.OrderBy(i => i))}}} {cultureName}={{{string.Join(",", translatedIndices.OrderBy(i => i))}}}");
        }

        Assert.True(mismatches.Count == 0,
            $"{cultureName} format placeholders differ from English (would throw FormatException at runtime):\n" + string.Join("\n", mismatches));
    }

    // The {N} argument indices a composite-format string references, ignoring escaped {{ and }} braces.
    private static HashSet<int> PlaceholderIndices(string value)
    {
        var cleaned = value.Replace("{{", "").Replace("}}", "");
        var indices = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(cleaned, @"\{(\d+)"))
            indices.Add(int.Parse(m.Groups[1].Value));
        return indices;
    }
}

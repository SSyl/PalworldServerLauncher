using System.Globalization;
using PalServerLauncher.Localization;

namespace PalServerLauncher.Config;

/// <summary>
/// Localized display text for a catalog setting. Looks up <c>Cat_&lt;Key&gt;_Label</c> / <c>Cat_&lt;Key&gt;_Desc</c>
/// in the string resources for the current UI culture, falling back to the catalog's own English when a key is
/// missing or blank. The setting Key itself never localizes (it is the literal ini key you type), only the
/// human-readable label and description do.
/// </summary>
public static class CatalogText
{
    public static string Label(GameSetting setting) =>
        Lookup($"Cat_{setting.Key}_Label") ?? setting.Label;

    public static string Description(GameSetting setting) =>
        Lookup($"Cat_{setting.Key}_Desc") ?? setting.Description;

    /// <summary>Localized display label for one enum option value of a setting. The value stored in the config
    /// stays canonical, only the dropdown display is translated.</summary>
    public static string Option(string settingKey, string value) =>
        Lookup($"Cat_{settingKey}_Opt_{value}") ?? value;

    private static string? Lookup(string key)
    {
        var value = Strings.ResourceManager.GetString(key, CultureInfo.CurrentUICulture);
        return string.IsNullOrEmpty(value) ? null : value;
    }
}

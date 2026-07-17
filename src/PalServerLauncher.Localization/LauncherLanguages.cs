namespace PalServerLauncher.Localization;

/// <summary>One selectable UI language: its culture <see cref="Code"/> (e.g. "en", "zh-Hans") and the
/// <see cref="DisplayName"/> shown in the picker. Display names are endonyms (each written in its own
/// language), so they are intentionally NOT localized.</summary>
public sealed record LauncherLanguage(string Code, string DisplayName);

/// <summary>The languages the launcher ships translations for. Add an entry here when a new satellite
/// resx (Strings.&lt;code&gt;.resx) is added, so it appears in the first-run picker and Launcher Settings.</summary>
public static class LauncherLanguages
{
    public static readonly IReadOnlyList<LauncherLanguage> All = new[]
    {
        new LauncherLanguage("en", "English"),
        new LauncherLanguage("de", "Deutsch"),
        new LauncherLanguage("es", "Español"),
        new LauncherLanguage("fr", "Français"),
        new LauncherLanguage("pt-BR", "Português (Brasil)"),
        new LauncherLanguage("ru", "Русский"),
        new LauncherLanguage("ja", "日本語"),
        new LauncherLanguage("zh-Hans", "简体中文"),
        new LauncherLanguage("zh-Hant", "繁體中文"),
        new LauncherLanguage("ko", "한국어"),
    };

    /// <summary>The entry for a code, or English if the code is unknown or blank.</summary>
    public static LauncherLanguage ForCode(string? code) =>
        All.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase)) ?? All[0];
}

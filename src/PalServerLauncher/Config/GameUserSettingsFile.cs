using System.Text.RegularExpressions;

namespace PalServerLauncher.Config;

/// <summary>
/// The one key the launcher needs out of GameUserSettings.ini: <c>DedicatedServerName</c>, which holds the name of
/// the folder under <c>Pal/Saved/SaveGames/0</c> that the server loads as its world. The server does not scan for
/// saves. When the key is missing, or names a folder that isn't there, it mints a fresh GUID and writes it back, so
/// a world copied in without this key stays on disk and invisible in game (verified against a live server).
/// Text in, text out, so it's unit-tested. Every other line of the file is preserved byte for byte.
/// </summary>
public static class GameUserSettingsFile
{
    public const string FileName = "GameUserSettings.ini";
    private const string Section = "[/Script/Pal.PalGameLocalSettings]";

    // [^\r\n]* rather than .*, because . matches \r: that would swallow the CR and leave the rewritten line LF-only
    // in an otherwise CRLF file.
    private static readonly Regex KeyLine = new(
        @"^[ \t]*DedicatedServerName[ \t]*=[^\r\n]*",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The world folder the file names, or null when the key is absent or blank.</summary>
    public static string? ReadWorldId(string? iniText)
    {
        if (string.IsNullOrEmpty(iniText))
            return null;

        var match = KeyLine.Match(iniText);
        if (!match.Success)
            return null;

        var value = match.Value[(match.Value.IndexOf('=') + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Point the file at <paramref name="worldId"/>, rewriting the existing key in place, or adding it
    /// under the settings section (creating that section only if the file doesn't have it).</summary>
    public static string SetWorldId(string? iniText, string worldId)
    {
        var text = iniText ?? "";
        // CRLF unless the file is demonstrably LF-only, so a file we create from nothing matches what the
        // Windows server writes.
        var newline = text.Contains('\n') && !text.Contains("\r\n", StringComparison.Ordinal) ? "\n" : "\r\n";
        var line = $"DedicatedServerName={worldId}";

        // Every occurrence, not just the first: a hand-edited file can end up with two DedicatedServerName lines,
        // and rewriting them all means we're correct whichever one the engine honors.
        // MatchEvaluator, not a replacement string: a $ in the value would otherwise be read as a substitution.
        if (KeyLine.IsMatch(text))
            return KeyLine.Replace(text, _ => line);

        var section = text.IndexOf(Section, StringComparison.OrdinalIgnoreCase);
        if (section < 0)
        {
            var separator = text.Length == 0 || text.EndsWith('\n') ? "" : newline;
            return $"{text}{separator}{Section}{newline}{line}{newline}";
        }

        var endOfSectionLine = text.IndexOf('\n', section);
        return endOfSectionLine < 0
            ? $"{text}{newline}{line}{newline}"
            : $"{text[..(endOfSectionLine + 1)]}{line}{newline}{text[(endOfSectionLine + 1)..]}";
    }
}

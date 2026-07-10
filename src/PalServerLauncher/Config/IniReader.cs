using System.Globalization;
using System.IO;
using System.Text;

namespace PalServerLauncher.Config;

/// <summary>
/// Read-only extraction of the few keys the launcher needs from PalWorldSettings.ini.
///
/// Palworld stores every gameplay/server setting as one giant single-line tuple under
/// <c>[/Script/Pal.PalGameWorldSettings]</c>:
/// <code>
/// OptionSettings=(Difficulty=None,...,AdminPassword="secret",RESTAPIEnabled=True,RESTAPIPort=8212,PublicPort=8211,...)
/// </code>
/// We parse that blob but never rewrite it (v1 writes no game ini). The splitter is quote-aware
/// so commas/equals inside a quoted value (e.g. a password) don't break parsing.
/// </summary>
public static class IniReader
{
    public static PalworldServerSettings ReadFile(string palWorldSettingsPath)
    {
        if (string.IsNullOrWhiteSpace(palWorldSettingsPath) || !File.Exists(palWorldSettingsPath))
            return new PalworldServerSettings();

        return Parse(File.ReadAllText(palWorldSettingsPath));
    }

    /// <summary>Parse the full ini text (or just the OptionSettings line) into the keys we care about.</summary>
    public static PalworldServerSettings Parse(string iniText)
    {
        var options = ExtractOptionSettings(iniText);

        return new PalworldServerSettings
        {
            RestApiPort = TryGetInt(options, "RESTAPIPort"),
            RestApiEnabled = TryGetBool(options, "RESTAPIEnabled"),
            AdminPassword = options.TryGetValue("AdminPassword", out var pw) ? pw : null,
            PublicPort = TryGetInt(options, "PublicPort"),
        };
    }

    /// <summary>
    /// Pull the <c>OptionSettings=(...)</c> tuple into a case-insensitive key -> value map.
    /// Values keep no surrounding quotes; basic <c>\"</c> and <c>\\</c> escapes are unescaped.
    /// </summary>
    private static Dictionary<string, string> ExtractOptionSettings(string iniText)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var optionLine = FindOptionSettingsLine(iniText);
        if (optionLine is null)
            return result;

        // Strip the leading "OptionSettings=" and one enclosing pair of parens.
        var inner = optionLine[(optionLine.IndexOf('=') + 1)..].Trim();
        if (inner.StartsWith('(') && inner.EndsWith(')'))
            inner = inner[1..^1];

        foreach (var pair in SplitTopLevel(inner))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = pair[..eq].Trim();
            var value = pair[(eq + 1)..].Trim();
            result[key] = Unquote(value);
        }

        return result;
    }

    private static string? FindOptionSettingsLine(string iniText)
    {
        using var reader = new StringReader(iniText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.TrimStart().StartsWith("OptionSettings=", StringComparison.OrdinalIgnoreCase))
                return line.Trim();
        }
        return null;
    }

    /// <summary>Split on commas that are not inside a double-quoted span.</summary>
    private static IEnumerable<string> SplitTopLevel(string inner)
    {
        var sb = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
            }
            else if (c == ',' && !inQuotes)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        if (sb.Length > 0)
            yield return sb.ToString();
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
            value = value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return value;
    }

    private static int? TryGetInt(IReadOnlyDictionary<string, string> map, string key) =>
        map.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static bool? TryGetBool(IReadOnlyDictionary<string, string> map, string key)
    {
        if (!map.TryGetValue(key, out var raw))
            return null;
        // Palworld writes True/False; be lenient about casing and whitespace.
        return raw.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }
}

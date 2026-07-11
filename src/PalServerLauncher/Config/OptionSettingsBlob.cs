using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PalServerLauncher.Config;

/// <summary>
/// Round-trips the single-line <c>OptionSettings=(...)</c> blob in PalWorldSettings.ini, the ~90
/// gameplay/server keys Palworld stores as one compact tuple under
/// <c>[/Script/Pal.PalGameWorldSettings]</c>. It parses the blob into an ORDERED (key, raw-value)
/// list, preserving every key (including ones the launcher doesn't recognise) and each value's exact
/// raw text, lets specific keys be edited, and re-emits the file with only the OptionSettings line
/// rebuilt, every other line and byte untouched.
///
/// This is the riskiest file in the project (the old design deliberately avoided writing game ini):
/// edits are gated to a stopped server, unknown keys are never touched, and the identity round-trip
/// (Load -> Render with no edits == input) is unit-tested.
/// </summary>
public sealed class OptionSettingsBlob
{
    private readonly string _iniText;
    private readonly string _originalOptionLine;
    private readonly List<KeyValuePair<string, string>> _pairs;

    /// <summary>False when the file has no <c>OptionSettings=</c> line (e.g. an empty/uninitialised ini).</summary>
    public bool HasOptionSettings { get; }

    private OptionSettingsBlob(string iniText, string originalOptionLine, List<KeyValuePair<string, string>> pairs, bool has)
    {
        _iniText = iniText;
        _originalOptionLine = originalOptionLine;
        _pairs = pairs;
        HasOptionSettings = has;
    }

    public static OptionSettingsBlob Load(string iniText)
    {
        var line = FindOptionLine(iniText);
        if (line is null)
            return new OptionSettingsBlob(iniText, "", new(), has: false);

        var inner = line[(line.IndexOf('=') + 1)..].Trim();
        if (inner.StartsWith('(') && inner.EndsWith(')'))
            inner = inner[1..^1];

        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var part in SplitTopLevel(inner))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var key = part[..eq].Trim();
            var raw = part[(eq + 1)..]; // keep the raw value EXACTLY so unedited keys re-emit identically
            pairs.Add(new KeyValuePair<string, string>(key, raw));
        }

        return new OptionSettingsBlob(iniText, line, pairs, has: true);
    }

    /// <summary>Keys present in the blob, in file order.</summary>
    public IReadOnlyList<string> Keys => _pairs.Select(p => p.Key).ToList();

    /// <summary>The exact raw value text (quotes included), or null if the key is absent.</summary>
    public string? GetRaw(string key)
    {
        foreach (var pair in _pairs)
            if (pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        return null;
    }

    /// <summary>The unquoted value, or null if the key is absent.</summary>
    public string? GetValue(string key)
    {
        var raw = GetRaw(key);
        return raw is null ? null : Unquote(raw.Trim());
    }

    public bool? GetBool(string key)
    {
        var v = GetValue(key);
        return v is null ? null : v.Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
    }

    public int? GetInt(string key) =>
        int.TryParse(GetValue(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public double? GetFloat(string key) =>
        double.TryParse(GetValue(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Set a key's raw value verbatim (caller owns quoting). Updates in place, or appends if new.</summary>
    public void SetRaw(string key, string rawValue)
    {
        for (var i = 0; i < _pairs.Count; i++)
        {
            if (_pairs[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                _pairs[i] = new KeyValuePair<string, string>(_pairs[i].Key, rawValue);
                return;
            }
        }
        _pairs.Add(new KeyValuePair<string, string>(key, rawValue));
    }

    public void SetString(string key, string value) => SetRaw(key, Quote(value));
    public void SetBool(string key, bool value) => SetRaw(key, value ? "True" : "False");
    public void SetInt(string key, int value) => SetRaw(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Palworld writes rates as fixed 6-decimal floats (e.g. 1.000000).</summary>
    public void SetFloat(string key, double value) => SetRaw(key, value.ToString("0.000000", CultureInfo.InvariantCulture));

    /// <summary>Enum/token values are written unquoted (e.g. Difficulty=None, DeathPenalty=All).</summary>
    public void SetEnum(string key, string token) => SetRaw(key, token);

    /// <summary>Re-emit the whole ini text with only the OptionSettings line rebuilt (indentation preserved).</summary>
    public string Render()
    {
        if (!HasOptionSettings)
            return _iniText;

        var inner = string.Join(",", _pairs.Select(p => $"{p.Key}={p.Value}"));
        var indent = _originalOptionLine[..(_originalOptionLine.Length - _originalOptionLine.TrimStart().Length)];
        var rebuilt = $"{indent}OptionSettings=({inner})";
        return _iniText.Replace(_originalOptionLine, rebuilt);
    }

    private static string? FindOptionLine(string iniText)
    {
        using var reader = new StringReader(iniText);
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (line.TrimStart().StartsWith("OptionSettings=", StringComparison.OrdinalIgnoreCase))
                return line;
        return null;
    }

    /// <summary>
    /// Split on commas that are at the TOP level only: not inside a double-quoted span, and not inside
    /// a nested parenthesized value such as <c>CrossplayPlatforms=(Steam,Xbox,PS5,Mac)</c>. Palworld's
    /// blob really contains such tuples; without tracking paren depth their inner commas would split the
    /// tuple into fragments and drop its closing paren, corrupting the whole line on re-render.
    /// </summary>
    private static IEnumerable<string> SplitTopLevel(string inner)
    {
        var sb = new StringBuilder();
        var inQuotes = false;
        var depth = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (inQuotes && c == '\\' && i + 1 < inner.Length)
            {
                // Inside a quoted value a backslash escapes the next char (matches Quote()): consume
                // both so an escaped \" doesn't flip quote state and desync the split, otherwise the
                // value swallows every following key up to the next balanced quote.
                sb.Append(c);
                sb.Append(inner[++i]);
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                sb.Append(c);
            }
            else if (c == '(' && !inQuotes)
            {
                depth++;
                sb.Append(c);
            }
            else if (c == ')' && !inQuotes)
            {
                if (depth > 0) depth--;
                sb.Append(c);
            }
            else if (c == ',' && !inQuotes && depth == 0)
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

    private static string Quote(string value) =>
        $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
            value = value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
        return value;
    }
}

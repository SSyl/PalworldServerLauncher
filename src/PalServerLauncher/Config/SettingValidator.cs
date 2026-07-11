using System.Globalization;

namespace PalServerLauncher.Config;

/// <summary>
/// Pure input validation for the settings editor, keyed off <see cref="SettingType"/>. Split into a
/// character-level gate (what may be typed at all) and a value-level check (whether the assembled text
/// is a valid, in-range value). WPF-free so it is unit-testable; the dialog wires it to
/// PreviewTextInput (char gate), a paste handler, and TextChanged / Save (live red + blocking).
/// </summary>
public static class SettingValidator
{
    /// <summary>
    /// True if <paramref name="c"/> may be typed into a field of this type. Int -> digits; Float ->
    /// digits and a decimal point; Text -> anything except the quote/backslash that break the blob (and
    /// which Palworld can't represent). Bool/Enum are constrained by their control -> always allowed.
    /// This is a coarse gate: it permits partial/malformed values (e.g. "1..5") that <see cref="Validate"/>
    /// then rejects, which is why the box can turn red even when every character was individually allowed.
    /// </summary>
    public static bool IsCharAllowed(SettingType type, char c) => type switch
    {
        SettingType.Int => char.IsAsciiDigit(c),
        SettingType.Float => char.IsAsciiDigit(c) || c == '.',
        SettingType.IpAddress => char.IsAsciiDigit(c) || c == '.',
        SettingType.Text => c != '"' && c != '\\',
        // Raw (tuple/list) values keep commas/parens/quotes so a value like (Steam,Xbox) or ("PALBOX")
        // is editable; only the backslash is blocked (it would desync the blob's escape handling).
        SettingType.Raw => c != '\\',
        _ => true,
    };

    /// <summary>True when every character in <paramref name="text"/> is allowed for the type (paste filtering).</summary>
    public static bool IsTextAllowed(SettingType type, string text)
    {
        foreach (var c in text)
            if (!IsCharAllowed(type, c))
                return false;
        return true;
    }

    /// <summary>
    /// Whether two raw strings are the SAME once read as the given type, so Save doesn't rewrite a key the
    /// user never really changed. A checkbox reports the canonical "True"/"False", so a hand-edited
    /// <c>bHardcore=false</c> would otherwise look changed; likewise <c>1.0</c> vs <c>1.000000</c> and enum
    /// casing. Text / IpAddress compare exactly (their formatting is meaningful). Falls back to a literal
    /// compare when a numeric value doesn't parse, so a malformed original isn't silently treated as equal.
    /// </summary>
    public static bool ValuesEqual(SettingType type, string a, string b)
    {
        switch (type)
        {
            case SettingType.Bool:
                return ParseBool(a) == ParseBool(b);
            case SettingType.Int:
                return int.TryParse(a.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ia)
                    && int.TryParse(b.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ib)
                        ? ia == ib
                        : a.Trim() == b.Trim();
            case SettingType.Float:
                return double.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
                    && double.TryParse(b.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
                        ? da.Equals(db)
                        : a.Trim() == b.Trim();
            case SettingType.Enum:
                return a.Trim().Equals(b.Trim(), StringComparison.OrdinalIgnoreCase);
            default: // Text, IpAddress
                return a == b;
        }
    }

    /// <summary>Palworld bools are True/False; be lenient about casing and accept 1 (as the ini writer does).</summary>
    private static bool ParseBool(string value) =>
        value.Trim() is var v && (v.Equals("True", StringComparison.OrdinalIgnoreCase) || v == "1");

    /// <summary>
    /// Validate the assembled value. Returns (true, "") when acceptable, else (false, reason) where
    /// reason completes "must be ..." for the Save error prompt (e.g. "a whole number between 1 and
    /// 65535"). Empty/whitespace is allowed unless <paramref name="required"/>.
    /// </summary>
    public static (bool Ok, string Reason) Validate(
        SettingType type, string text, double? min = null, double? max = null, bool required = false)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return required ? (false, "provided") : (true, "");

        switch (type)
        {
            case SettingType.Int:
                if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return (false, "a whole number");
                return RangeReason(i, min, max, "a whole number");
            case SettingType.Float:
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    return (false, "a number");
                return RangeReason(d, min, max, "a number");
            case SettingType.Text:
                return IsTextAllowed(type, text) ? (true, "") : (false, "text without quotes or backslashes");
            case SettingType.IpAddress:
                // IPv4-only by design: Palworld documents -publicip as `x.x.x.x` and never mentions IPv6.
                // To add IPv6 later, this is the whole change: allow hex digits + ':' in IsCharAllowed
                // (the typing gate) and also accept IPAddress.TryParse(..) as InterNetworkV6 here.
                return IsIPv4(trimmed) ? (true, "") : (false, "a valid IPv4 address (e.g. 203.0.113.5) or blank");
            case SettingType.Raw:
                return IsWellFormedRaw(trimmed) ? (true, "") : (false, "balanced parentheses and quotes with no stray commas");
            default:
                return (true, ""); // Bool/Enum are constrained by their control
        }
    }

    /// <summary>
    /// A Raw (tuple/list) value is well-formed when its parentheses and double-quotes are balanced and it
    /// has no comma at the top level, a top-level comma would split it into two blob entries and drop data.
    /// Mirrors the structure <c>OptionSettingsBlob.SplitTopLevel</c> relies on, so the field can turn red
    /// live instead of only failing the save-time round-trip guard.
    /// </summary>
    private static bool IsWellFormedRaw(string value)
    {
        var depth = 0;
        var inQuotes = false;
        foreach (var c in value)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (inQuotes) continue;
            else if (c == '(') depth++;
            else if (c == ')' && --depth < 0) return false;
            else if (c == ',' && depth == 0) return false;
        }
        return depth == 0 && !inQuotes;
    }

    private static (bool, string) RangeReason(double value, double? min, double? max, string noun)
    {
        if (min is not null && max is not null && (value < min || value > max))
            return (false, $"{noun} between {Format(min.Value)} and {Format(max.Value)}");
        if (min is not null && value < min)
            return (false, $"{noun} {Format(min.Value)} or greater");
        if (max is not null && value > max)
            return (false, $"{noun} {Format(max.Value)} or less");
        return (true, "");
    }

    private static string Format(double v) =>
        v == Math.Floor(v) ? ((long)v).ToString(CultureInfo.InvariantCulture) : v.ToString(CultureInfo.InvariantCulture);

    /// <summary>Strict dotted-quad IPv4 check: exactly four octets, each a 0-255 integer.</summary>
    private static bool IsIPv4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4)
            return false;
        foreach (var part in parts)
            if (part.Length is < 1 or > 3 || !byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                return false;
        return true;
    }
}

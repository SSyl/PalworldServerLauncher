using System;

namespace PalServerLauncher.Config;

/// <summary>
/// Pure matcher for the Server Settings search box. A setting matches when EVERY whitespace-separated token
/// in the query appears (case-insensitively) somewhere in its ini key, its label, or its description. This is
/// token-AND, not phrase, matching, so "Recreate Character" finds a key whose two words are split across its
/// name and description ("bCharacterRecreateInHardcore" + "recreate your character upon death"), while a lone
/// "Death" still finds it through the description and "bCharacter" through the raw key.
///
/// The key is always the literal (English) ini variable name, so it stays searchable in any UI language. The
/// label and description are whatever the caller passes, which is the localized text for the current culture,
/// so a Japanese description is searched in Japanese. CJK queries carry no spaces, so they match as a single
/// substring, which is the intended behavior for languages without word breaks.
/// </summary>
public static class SettingsSearch
{
    public static bool Matches(string? query, string key, string label, string description)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var haystack = key + '\n' + label + '\n' + description;
        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        return true;
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace PalServerLauncher.Core;

/// <summary>
/// Reads SteamCMD's Workshop state file (<c>steamapps\workshop\appworkshop_&lt;appid&gt;.acf</c>), a Valve
/// KeyValues document, to learn what content is currently in SteamCMD's cache for each Workshop item. The
/// launcher keys the update-detection gate on each item's <c>manifest</c> (a content id that changes only when
/// the item's content changes), so an unchanged mod isn't re-copied into the server's <c>Mods\Workshop</c> on
/// every start. The parser is a tolerant KeyValues reader, pure so it's unit-tested against a real ACF.
/// </summary>
public static class WorkshopManifest
{
    /// <summary>The installed state of one Workshop item in SteamCMD's cache.</summary>
    public sealed record ItemState(string Manifest, long TimeUpdated);

    /// <summary>Parse the ACF and return the installed state per Workshop id under <c>WorkshopItemsInstalled</c>.
    /// Empty if that section is absent or the text can't be parsed. Items without a manifest are skipped.</summary>
    public static IReadOnlyDictionary<string, ItemState> ParseInstalled(string acfText)
    {
        var result = new Dictionary<string, ItemState>(StringComparer.Ordinal);
        var root = KeyValues.Parse(acfText);
        // The document's single top node is "AppWorkshop", be defensive if that wrapper is missing.
        var app = root.Child("AppWorkshop") ?? root;
        var installed = app.Child("WorkshopItemsInstalled");
        if (installed is null)
            return result;

        foreach (var (id, item) in installed.Children)
        {
            var manifest = item.Leaf("manifest");
            if (string.IsNullOrEmpty(manifest))
                continue;
            var timeUpdated = long.TryParse(item.Leaf("timeupdated"), out var t) ? t : 0L;
            result[id] = new ItemState(manifest, timeUpdated);
        }
        return result;
    }

    /// <summary>A minimal Valve KeyValues node: either a leaf (<see cref="Value"/>) or a block of named children.</summary>
    private sealed class KeyValues
    {
        public string? Value { get; private init; }
        public Dictionary<string, KeyValues> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public KeyValues? Child(string key) => Children.TryGetValue(key, out var node) ? node : null;
        public string? Leaf(string key) => Child(key)?.Value;

        public static KeyValues Parse(string text)
        {
            var tokens = Tokenize(text);
            var pos = 0;
            var root = new KeyValues();
            ParseMembers(root, tokens, ref pos);
            return root;
        }

        /// <summary>Parse <c>key value</c> pairs into <paramref name="node"/> until a closing brace or end of input.</summary>
        private static void ParseMembers(KeyValues node, List<string> tokens, ref int pos)
        {
            while (pos < tokens.Count && tokens[pos] != "}")
            {
                var key = tokens[pos++];
                if (pos >= tokens.Count)
                    break;
                if (tokens[pos] == "{")
                {
                    pos++; // consume "{"
                    var child = new KeyValues();
                    ParseMembers(child, tokens, ref pos);
                    if (pos < tokens.Count && tokens[pos] == "}")
                        pos++; // consume "}"
                    node.Children[key] = child;
                }
                else
                {
                    node.Children[key] = new KeyValues { Value = tokens[pos++] };
                }
            }
        }

        /// <summary>Split the text into quoted-string, "{" and "}" tokens, skipping whitespace and // comments.</summary>
        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c is '{' or '}') { tokens.Add(c.ToString()); i++; continue; }
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    var sb = new StringBuilder();
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\' && i + 1 < text.Length) i++; // \" and \\ -> take the next char literally
                        sb.Append(text[i]);
                        i++;
                    }
                    i++; // consume closing quote
                    tokens.Add(sb.ToString());
                    continue;
                }
                // Unquoted token (rare in ACF): read up to whitespace or a brace.
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
                tokens.Add(text[start..i]);
            }
            return tokens;
        }
    }
}

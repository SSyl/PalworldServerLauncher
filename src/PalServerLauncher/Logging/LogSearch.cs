using System;

namespace PalServerLauncher.Logging;

/// <summary>
/// Matcher for the log filter box. Token-AND over the line's text and its source tag, the same rule as
/// <see cref="Config.SettingsSearch"/>. The tag is searched so "error" or "chat" filters by source. The
/// timestamp is not: it prefixes every line, so "1" would match nearly all of them.
/// </summary>
public static class LogSearch
{
    public static bool Matches(string? query, LogEntry entry) =>
        Matches(query, LogEntry.TagFor(entry.Channel, entry.Level), entry.Text);

    public static bool Matches(string? query, string tag, string text)
    {
        if (string.IsNullOrWhiteSpace(query))
            return true;

        var haystack = tag + '\n' + text;
        foreach (var token in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (haystack.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        return true;
    }
}

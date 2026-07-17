using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace PalServerLauncher.Rcon;

/// <summary>
/// Recent-command history for the RCON console, persisted to <c>rcon-history.json</c>. Most-recent first,
/// de-duplicated, and capped. The list update (<see cref="Add"/>) is pure so it's unit-tested; the file I/O is
/// best-effort (a missing or unreadable file just yields an empty history, and a failed save is swallowed).
/// Commands are stored verbatim whether or not they were valid, matching the barebones console.
/// </summary>
public static class RconHistory
{
    public const int MaxEntries = 50;

    /// <summary>Return a new history with <paramref name="command"/> moved to the front, any earlier duplicate of
    /// it removed, and the list capped at <see cref="MaxEntries"/>. A blank command is ignored (returns a copy).</summary>
    public static List<string> Add(IReadOnlyList<string> existing, string command)
    {
        var trimmed = (command ?? string.Empty).Trim();
        if (trimmed.Length == 0)
            return new List<string>(existing);

        var result = new List<string>(existing.Count + 1) { trimmed };
        foreach (var entry in existing)
            if (!string.Equals(entry, trimmed, StringComparison.Ordinal))
                result.Add(entry);

        if (result.Count > MaxEntries)
            result.RemoveRange(MaxEntries, result.Count - MaxEntries);
        return result;
    }

    /// <summary>Load the saved history, or an empty list if the file is missing or unreadable.</summary>
    public static List<string> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new List<string>()
                : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Persist the history, best-effort (a history we can't write is not worth surfacing).</summary>
    public static void Save(string path, IReadOnlyList<string> commands)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(commands));
        }
        catch
        {
            // Ignored on purpose.
        }
    }
}

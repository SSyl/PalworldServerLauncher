using System.Collections.Generic;
using System.IO;

namespace PalServerLauncher.Rcon;

/// <summary>
/// Best-effort disk persistence for the RCON console transcript (<c>rcon-log.txt</c>), one line per line, so
/// the log survives an app restart. Load returns an empty list if the file is missing or unreadable, Save
/// swallows write errors: a log we can't read or persist is not worth surfacing. The caller caps the line count.
/// </summary>
public static class RconTranscript
{
    public static List<string> Load(string path)
    {
        try
        {
            return File.Exists(path) ? new List<string>(File.ReadAllLines(path)) : new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void Save(string path, IReadOnlyList<string> lines)
    {
        try
        {
            File.WriteAllLines(path, lines);
        }
        catch
        {
            // Ignored on purpose.
        }
    }
}

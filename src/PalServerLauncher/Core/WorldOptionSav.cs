namespace PalServerLauncher.Core;

/// <summary>
/// Helpers for Palworld's <c>WorldOption.sav</c>, the per-world settings file that ships with a save
/// converted from a local/co-op world. On a dedicated server it overrides <c>PalWorldSettings.ini</c>
/// (including the REST API keys), which can leave the launcher unable to monitor/control the server. The
/// launcher offers to rename it to <c>.bak</c> so the server reads the ini instead. Detection/rename I/O
/// lives on <see cref="ServerController"/>; the collision-safe target name is pure so it can be unit-tested.
/// </summary>
public static class WorldOptionSav
{
    public const string FileName = "WorldOption.sav";

    /// <summary>
    /// The path to rename <paramref name="savPath"/> to: <c>&lt;path&gt;.bak</c>, or <c>.bak.1</c>,
    /// <c>.bak.2</c>, ... if that already exists, so an existing backup is never clobbered.
    /// <paramref name="exists"/> reports whether a candidate path is already taken (File.Exists in prod).
    /// </summary>
    public static string BakTargetPath(string savPath, Func<string, bool> exists)
    {
        var candidate = savPath + ".bak";
        if (!exists(candidate))
            return candidate;
        for (var n = 1; ; n++)
        {
            var numbered = $"{savPath}.bak.{n}";
            if (!exists(numbered))
                return numbered;
        }
    }
}

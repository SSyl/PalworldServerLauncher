using System.Globalization;
using System.Windows.Media;

namespace PalServerLauncher.Logging;

/// <summary>Severity of a launcher-authored line. Lines from SteamCMD, the server, chat, and the player roster
/// carry no severity of their own and are all <see cref="Info"/>, so they are colored by channel instead.</summary>
public enum LogLevel
{
    Info,
    Debug,
    Error,
}

/// <summary>
/// One line as the UI holds it. The time, channel, and severity stay as data rather than being baked into a
/// display string, so the tabs can filter, color, and re-render existing lines when a view option changes.
/// The log file is written separately by <see cref="Logger"/> and keeps its own fuller format.
/// </summary>
public sealed record LogEntry(DateTime Time, LogChannel Channel, LogLevel Level, string Text)
{
    /// <summary>The short source/severity name, shared with the log file so the two can never drift.</summary>
    public static string TagFor(LogChannel channel, LogLevel level) => channel switch
    {
        LogChannel.SteamCmd => "STEAM",
        LogChannel.Server => "SERVER",
        LogChannel.Chat => "CHAT",
        LogChannel.PlayerJoin => "PLAYER",
        _ => level switch
        {
            LogLevel.Error => "ERROR",
            LogLevel.Debug => "DEBUG",
            _ => "INFO",
        },
    };

    /// <summary>The tag as a column, brackets tight around the word and padded after them so the message starts
    /// at the same place on every line. Eight wide, set by the longest tags, SERVER and PLAYER.</summary>
    public static string TagColumn(LogChannel channel, LogLevel level) => $"[{TagFor(channel, level)}]".PadRight(8);

    /// <summary>The rendered line. <paramref name="withTag"/> is on for the General tab, which mixes every
    /// source, and off for the filtered tabs where the tag would repeat on every row.</summary>
    public string Render(bool withDate, bool withTag)
    {
        // InvariantCulture, matching the log file, because interpolation would use the user's calendar and a
        // Thai regional setting renders 2569 for 2026 regardless of the launcher's own language.
        var format = withDate ? "'['yyyy-MM-dd HH:mm:ss']'" : "'['HH:mm:ss']'";
        var time = Time.ToString(format, CultureInfo.InvariantCulture);
        return withTag ? $"{time} {TagColumn(Channel, Level)} {Text}" : $"{time} {Text}";
    }

    public SolidColorBrush Brush => Theme.ForLog(Channel, Level);
}

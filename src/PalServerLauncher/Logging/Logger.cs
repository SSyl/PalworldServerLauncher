using System.Globalization;
using System.IO;
using System.Linq;
using PalServerLauncher.Config;

namespace PalServerLauncher.Logging;

/// <summary>
/// Single logging sink for the whole app. Writes every line to a timestamped session file under
/// <c>logs/</c> and mirrors lines to the UI via <see cref="LineForUi"/> tagged with a
/// <see cref="LogChannel"/> (the General tab shows all; SteamCmd/Server tabs filter to their own).
/// Thread-safe: callbacks come from background threads (SteamCMD/server log tailers, process events).
/// Debug lines are emitted only in verbose mode.
/// </summary>
public sealed class Logger
{
    private readonly object _sync = new();
    private readonly bool _echoToConsole;

    public bool Verbose { get; }
    public string FilePath { get; }

    /// <summary>Raised for every line, as a <see cref="LogEntry"/> for the UI tabs.</summary>
    public event Action<LogEntry>? LineForUi;

    /// <param name="echoToConsole">Also write every line to stdout (for CLI/console use).</param>
    public Logger(bool verbose, bool echoToConsole = false)
    {
        Verbose = verbose;
        _echoToConsole = echoToConsole;
        var dir = Path.Combine(LauncherConfig.DataRoot, LauncherConfig.LogsFolderName);
        Directory.CreateDirectory(dir);
        // Dots rather than colons in the time, since a colon is not legal in a Windows filename.
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH.mm.ss", CultureInfo.InvariantCulture);
        FilePath = Path.Combine(dir, $"launcher-{stamp}.log");
        PruneOldLogs(dir, keep: 10);
        Info($"=== Log started ({(verbose ? "verbose" : "normal")} mode) -> {FilePath} ===");
    }

    public void Info(string message) => Emit(LogLevel.Info, message, message, LogChannel.General);

    /// <summary>Detailed diagnostics, written only when verbose (--debug / --verbose).</summary>
    public void Debug(string message)
    {
        if (Verbose) Emit(LogLevel.Debug, message, message, LogChannel.General);
    }

    public void Error(string message, Exception? ex = null)
    {
        var fileText = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        var uiText = ex is null ? message : $"{message}: {ex.Message}";
        Emit(LogLevel.Error, fileText, uiText, LogChannel.General);
    }

    /// <summary>A line from SteamCMD (its own console_log.txt / invocation bookends).</summary>
    public void SteamCmd(string message) => Emit(LogLevel.Info, message, message, LogChannel.SteamCmd);

    /// <summary>A line captured from the game server's stdout/stderr (Palworld writes no log file).</summary>
    public void Server(string message) => Emit(LogLevel.Info, message, message, LogChannel.Server);

    /// <summary>An in-game chat line captured from the server's stdout.</summary>
    public void Chat(string message) => Emit(LogLevel.Info, message, message, LogChannel.Chat);

    /// <summary>A player join/leave line (derived from the REST /players roster diff).</summary>
    public void PlayerJoin(string message) => Emit(LogLevel.Info, message, message, LogChannel.PlayerJoin);

    /// <summary>One line as it goes to the file. Full date per line rather than only in the filename, because a
    /// server runs for days and people paste a few lines into a bug report where the filename doesn't travel
    /// with them. InvariantCulture is load-bearing, since interpolation would use the user's calendar and a Thai
    /// regional setting writes 2569 for 2026, which the launcher could then no longer parse back.</summary>
    public static string FileLine(DateTime now, LogChannel channel, LogLevel level, string fileText) =>
        $"[{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] " +
        $"{LogEntry.TagColumn(channel, level)} {fileText}";

    /// <summary>
    /// The entry the tabs get, or null when the line is worth writing to the file but not worth a row. The file
    /// keeps its line verbatim while the UI shows one timestamp, so a source's own stamp is split off and
    /// preferred where it exists, since Palworld's player and chat lines reach us up to a minute after the
    /// event. A line left with nothing but a timestamp is dropped rather than shown as an empty row.
    /// </summary>
    public static LogEntry? UiEntry(DateTime now, LogChannel channel, LogLevel level, string uiText)
    {
        var (sourceTime, text) = LogLinePrefix.Split(uiText);
        if (channel == LogChannel.Chat)
            text = LogLinePrefix.StripChatMarkers(text);
        return string.IsNullOrWhiteSpace(text) ? null : new LogEntry(sourceTime ?? now, channel, level, text);
    }

    private void Emit(LogLevel level, string fileText, string uiText, LogChannel channel)
    {
        var now = DateTime.Now;
        var line = FileLine(now, channel, level, fileText);
        lock (_sync)
        {
            // Two independent sinks, so a locked or unwritable log file doesn't also silence --console.
            try { File.AppendAllText(FilePath, line + Environment.NewLine); }
            catch { /* Never let a logging failure crash the app. */ }

            if (_echoToConsole)
            {
                try { Console.Out.WriteLine(line); }
                catch { }
            }
        }
        if (UiEntry(now, channel, level, uiText) is { } entry)
            LineForUi?.Invoke(entry);
    }

    private static void PruneOldLogs(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "launcher-*.log")
                .Select(path => (Path: path, Written: File.GetLastWriteTimeUtc(path)));
            foreach (var old in SelectExpired(files, keep))
                File.Delete(old);
        }
        catch
        {
            // Pruning is best-effort.
        }
    }

    /// <summary>The log paths beyond the newest <paramref name="keep"/>. Ordered by file time rather than by
    /// name, because an ordinal name sort put every "launcher-2026-08-11_..." before every older
    /// "launcher-20260811-..." (the '-' at 0x2D sorts under '0' at 0x30), so changing the filename format
    /// would have pruned the new files and kept the old ones forever.</summary>
    public static IReadOnlyList<string> SelectExpired(IEnumerable<(string Path, DateTime Written)> files, int keep) =>
        files.OrderByDescending(file => file.Written).Skip(keep).Select(file => file.Path).ToList();
}

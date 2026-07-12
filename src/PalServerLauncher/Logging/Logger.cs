using System.IO;
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

    /// <summary>Raised for every line: (channel, message text) for the UI tabs.</summary>
    public event Action<LogChannel, string>? LineForUi;

    /// <param name="echoToConsole">Also write every line to stdout (for CLI/console use).</param>
    public Logger(bool verbose, bool echoToConsole = false)
    {
        Verbose = verbose;
        _echoToConsole = echoToConsole;
        var dir = Path.Combine(LauncherConfig.DataRoot, LauncherConfig.LogsFolderName);
        Directory.CreateDirectory(dir);
        FilePath = Path.Combine(dir, $"launcher-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        PruneOldLogs(dir, keep: 10);
        Info($"=== Log started ({(verbose ? "verbose" : "normal")} mode) -> {FilePath} ===");
    }

    public void Info(string message) => Emit("INFO", message, message, LogChannel.General);

    /// <summary>Detailed diagnostics, written only when verbose (--debug / --verbose).</summary>
    public void Debug(string message)
    {
        if (Verbose) Emit("DEBUG", message, message, LogChannel.General);
    }

    public void Error(string message, Exception? ex = null)
    {
        var fileText = ex is null ? message : $"{message}{Environment.NewLine}{ex}";
        var uiText = ex is null ? message : $"{message}: {ex.Message}";
        Emit("ERROR", fileText, uiText, LogChannel.General);
    }

    /// <summary>A line from SteamCMD (its own console_log.txt / invocation bookends).</summary>
    public void SteamCmd(string message) => Emit("STEAM", message, message, LogChannel.SteamCmd);

    /// <summary>A line captured from the game server's stdout/stderr (Palworld writes no log file).</summary>
    public void Server(string message) => Emit("SERVER", message, message, LogChannel.Server);

    /// <summary>An in-game chat line captured from the server's stdout.</summary>
    public void Chat(string message) => Emit("CHAT", message, message, LogChannel.Chat);

    /// <summary>A player join/leave line (derived from the REST /players roster diff).</summary>
    public void PlayerJoin(string message) => Emit("PLAYER", message, message, LogChannel.PlayerJoin);

    private void Emit(string tag, string fileText, string uiText, LogChannel channel)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{tag,-6}] {fileText}";
        lock (_sync)
        {
            try
            {
                File.AppendAllText(FilePath, line + Environment.NewLine);
                if (_echoToConsole)
                    Console.Out.WriteLine(line);
            }
            catch
            {
                // Never let a logging failure crash the app.
            }
        }
        LineForUi?.Invoke(channel, uiText);
    }

    private static void PruneOldLogs(string dir, int keep)
    {
        try
        {
            var files = Directory.GetFiles(dir, "launcher-*.log");
            if (files.Length <= keep) return;
            Array.Sort(files, StringComparer.Ordinal); // timestamped names sort chronologically
            foreach (var old in files[..^keep])
                File.Delete(old);
        }
        catch
        {
            // Pruning is best-effort.
        }
    }
}

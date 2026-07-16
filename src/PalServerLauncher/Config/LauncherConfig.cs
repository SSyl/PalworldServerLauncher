using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PalServerLauncher.Config;

/// <summary>
/// The launcher's own settings, the only file we write at runtime. The game never touches
/// this, so it is safe to edit any time (unlike PalWorldSettings.ini, which we only read).
/// Serialized as indented JSON via <see cref="Load"/> / <see cref="Save"/>.
/// </summary>
public sealed class LauncherConfig
{
    /// <summary>Where the launcher keeps everything (settings, <c>steamcmd/</c>, <c>server/</c>,
    /// <c>backups/</c>, <c>logs/</c>) so the exe sits alone: a <c>PalworldServerLauncher/</c> folder next
    /// to the exe. This is the default <see cref="ServerRoot"/> and the folder for launcher.json + logs.</summary>
    public static string DataRoot => Path.Combine(AppContext.BaseDirectory, "PalworldServerLauncher");

    /// <summary>Name of the server install subfolder under <see cref="ServerRoot"/> (the SteamCMD install dir).</summary>
    public const string ServerFolderName = "PalworldDedicatedServer";

    /// <summary>Name of the launcher's own log subfolder under <see cref="DataRoot"/>.</summary>
    public const string LogsFolderName = "LauncherLogs";

    /// <summary>Folder that contains (or will contain) the server install, <c>steamcmd/</c>, and <c>backups/</c>.</summary>
    public string ServerRoot { get; set; } = DataRoot;

    // --- Server launch arguments (command-line; https://docs.palworldgame.com/settings-and-operation/arguments) ---
    /// <summary><c>-port</c>: the port the server listens on.</summary>
    public int ServerPort { get; set; } = 8211;

    /// <summary><c>-QueryPort</c>: the Steam query port. 0 = auto-pick the first free UDP port at launch (from 27015).</summary>
    public int QueryPort { get; set; } = 0;

    /// <summary><c>-players</c>: max players. 0 = don't pass it, defer to the ini's ServerPlayerMaxNum (the arg overrides the ini).</summary>
    public int MaxPlayers { get; set; } = 0;

    /// <summary><c>-useperfthreads -NoAsyncLoadingThread -UseMultithreadForDS</c> (recommended on).</summary>
    public bool PerformanceThreads { get; set; } = true;

    /// <summary><c>-NumberOfWorkerThreadsServer</c>: worker threads (0 = auto; only used with PerformanceThreads).</summary>
    public int WorkerThreads { get; set; } = 0;

    /// <summary><c>-publiclobby</c>: list as a community/public server (default = private).</summary>
    public bool CommunityServer { get; set; } = false;

    /// <summary><c>-publicip</c> (community only; blank = auto-detect).</summary>
    public string PublicIp { get; set; } = "";

    /// <summary><c>-publicport</c> (community only; 0 = auto. Does not change the listen port).</summary>
    public int PublicPortArg { get; set; } = 0;

    /// <summary><c>-logformat</c>: "Text" or "Json" (blank = don't pass).</summary>
    public string LogFormat { get; set; } = "";

    /// <summary>Any extra raw command-line args, space-separated (overflow for anything not exposed as a field).</summary>
    public string ExtraServerArgs { get; set; } = "";

    // --- Process tuning (applied to the server process after launch / adopt) ---
    /// <summary>Windows process priority for the server: BelowNormal / Normal / AboveNormal / High.</summary>
    public string ServerPriority { get; set; } = "Normal";
    /// <summary>CPU core affinity bitmask (bit i = core i allowed). 0 = all cores (no restriction).</summary>
    public long ServerAffinityMask { get; set; } = 0;

    // --- Restart ---
    public bool RestartOnCrash { get; set; } = true;
    public bool ScheduledRestartEnabled { get; set; } = false;

    /// <summary>Explicit times of day to restart at (e.g., 03:00, 18:00). Empty = no scheduled restarts.</summary>
    public List<TimeOnly> RestartTimes { get; set; } = new() { new TimeOnly(3, 0) };

    /// <summary>Don't let a scheduled restart fire on a server that only just came up (post-maintenance guard).</summary>
    public TimeSpan MinUptimeBeforeRestart { get; set; } = TimeSpan.FromHours(2);

    /// <summary>Announce staged in-game restart warnings (shared by update/scheduled/manual restarts).</summary>
    public bool RestartBroadcastEnabled { get; set; } = true;

    /// <summary>Minutes-before-restart to announce a warning at (largest = total lead). Up to 3 are used.</summary>
    public List<int> RestartBroadcastLeadMinutes { get; set; } = new() { 15, 5, 1 };

    /// <summary>In-game announcement for scheduled/manual restarts. <c>{minutes}</c> is replaced with the
    /// minutes remaining before shutdown; remove it for a fixed message.</summary>
    public string RestartAnnounceMessage { get; set; } = "Server restart in {minutes} minutes";

    /// <summary>In-game announcement for update-triggered restarts. <c>{minutes}</c> is replaced with the
    /// minutes remaining before shutdown; remove it for a fixed message.</summary>
    public string UpdateAnnounceMessage { get; set; } = "Server update available. Restarting server in {minutes} minutes";

    // --- Update ---
    /// <summary>Run the SteamCMD app_update on every Start / restart to stay current on boot. Off = never
    /// download updates on start/restart (an explicit "Update &amp; restart" and auto-update-while-running still apply).</summary>
    public bool UpdateOnStart { get; set; } = true;
    public bool AutoUpdateEnabled { get; set; } = true;

    /// <summary>How often to poll SteamCMD for a new build while running. Editable in the UI (whole minutes);
    /// applies on the next monitor start (mirrors <see cref="HealthProbeInterval"/>).</summary>
    public TimeSpan UpdateCheckInterval { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Run SteamCMD hidden (progress only in the SteamCMD tab) instead of in its own console window.</summary>
    public bool HideSteamCmdWindow { get; set; } = true;

    /// <summary>Add <c>validate</c> to the automatic update on Start (full file verification; slower).</summary>
    public bool VerifyOnUpdate { get; set; } = false;

    // --- Version pin (freezes the installed build, suppressing all update activity while set) ---
    /// <summary>Pin the server to a fixed build. While on, all automatic update activity is suppressed and the
    /// update controls gray out (the background monitor never starts, Start/restart won't app_update, and the
    /// manual check is disabled). Cleared to resume updates. The build it's pinned to is <see cref="PinnedBuildId"/>.</summary>
    public bool VersionPinEnabled { get; set; } = false;

    /// <summary>The build id the server is pinned to (shown in the UI and, later, the download_depot rollback
    /// target). Set from the installed build when the pin is enabled, empty when not pinned or the id is unknown.</summary>
    public string PinnedBuildId { get; set; } = "";

    // --- Backup ---
    public bool BackupOnStartup { get; set; } = true;
    public bool BackupOnShutdown { get; set; } = true;
    public int BackupRetentionDays { get; set; } = 7;

    /// <summary>Enable scheduled backups at the explicit <see cref="BackupTimes"/>.</summary>
    public bool ScheduledBackupEnabled { get; set; } = false;

    /// <summary>Explicit times of day to back up at (chosen via the times picker, like restarts).</summary>
    public List<TimeOnly> BackupTimes { get; set; } = new();

    // --- Health ---
    /// <summary>How often (seconds via TimeSpan) the launcher polls the REST API for status/health.</summary>
    public TimeSpan HealthProbeInterval { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>Restart a live-but-wedged server (REST unreachable / world frozen). Off = never auto-restart from health.</summary>
    public bool ZombieCheckEnabled { get; set; } = true;

    /// <summary>Consecutive failed health checks before a wedged server is treated as a zombie and restarted.
    /// Default 10 (~70s at the default probe interval) so a brief lag spike doesn't trigger a restart.</summary>
    public int ZombieFailureThreshold { get; set; } = 10;

    /// <summary>Periodically log a server status line (FPS | Players | Uptime | ...), handy in --console / CLI use.</summary>
    public bool LogHealthStats { get; set; } = false;

    // --- Discord webhook notifications (off by default) ---
    public bool DiscordEnabled { get; set; } = false;
    public string DiscordWebhookUrl { get; set; } = "";
    /// <summary>Notify on server up/down/update/backup/crash.</summary>
    public bool DiscordNotifyLifecycle { get; set; } = true;
    /// <summary>Notify on player join/leave (needs REST).</summary>
    public bool DiscordNotifyPlayers { get; set; } = false;

    // --- Discord bot control (off by default), lets a bot run server commands from Discord ---
    /// <summary>Enable the Discord bot that accepts slash commands (needs a bot token below).</summary>
    public bool DiscordBotEnabled { get; set; } = false;
    /// <summary>Bot token (a SECRET, never logged). Create a bot at the Discord Developer Portal.</summary>
    public string DiscordBotToken { get; set; } = "";
    /// <summary>Only accept commands in this channel (0 = no channel restriction). Lock it down in Discord -
    /// anyone who can post here can control the server.</summary>
    public ulong DiscordCommandChannelId { get; set; } = 0;
    /// <summary>Only accept commands from users with this role (0 = no role restriction).</summary>
    public ulong DiscordCommandRoleId { get; set; } = 0;
    /// <summary>Per-user cooldown between accepted commands, in seconds (throttles spam / fat-fingers).</summary>
    public int DiscordCommandCooldownSeconds { get; set; } = 5;
    /// <summary>Which bot commands are exposed, by command name. A command missing from the map falls back to
    /// its built-in default (see <see cref="Core.DiscordBotService.IsCommandEnabled"/>), so new commands added
    /// in a later version appear automatically and destructive ones stay off until an admin opts in.</summary>
    public Dictionary<string, bool> DiscordCommandEnabled { get; set; } = new();

    // --- UI ---
    /// <summary>UI language as a culture name (e.g. "en", "zh-Hans"). Applied at startup to
    /// <c>CurrentUICulture</c> only, so changing it needs a launcher restart. Regional number/date
    /// formatting stays on the OS setting. An unknown tag falls back to English.</summary>
    public string Language { get; set; } = "en";

    /// <summary>Collapse the settings sections (Restarts / Backups / Misc) for a compact, log-focused window.</summary>
    public bool CompactMode { get; set; } = false;

    // --- Modding (Steam Workshop + bring-your-own, deployed via Mods/PalModSettings.ini) ---
    /// <summary>Master switch: when on, the launcher manages mods (downloads Workshop ids + writes PalModSettings.ini on start).</summary>
    public bool ModsEnabled { get; set; } = false;
    /// <summary>Steam username for authenticated Workshop downloads. SteamCMD caches its own session, the launcher
    /// never stores the password. Empty when no account is connected.</summary>
    public string SteamUsername { get; set; } = "";
    /// <summary>The tracked mods: added Workshop ids (downloaded) plus any local/dropped-in mods.</summary>
    public List<ModEntry> Mods { get; set; } = new();

    [JsonIgnore] public static string DefaultPath => Path.Combine(DataRoot, "launcher.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Load config from <paramref name="path"/>, returning defaults if the file is missing or unreadable.
    /// Self-heals: if the on-disk file is missing fields (e.g., written by an older launcher version),
    /// it's rewritten with the full current schema so new settings appear on disk automatically.
    /// </summary>
    public static LauncherConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path))
            {
                var raw = File.ReadAllText(path);
                var config = JsonSerializer.Deserialize<LauncherConfig>(raw, JsonOptions) ?? new LauncherConfig();
                // Older versions defaulted ServerRoot to the exe folder; repoint those at the data folder
                // (the file migration moves the actual server/steamcmd/backups data to match).
                if (PathsEqual(config.ServerRoot, AppContext.BaseDirectory))
                    config.ServerRoot = DataRoot;
                NormalizeFileToCurrentSchema(config, raw, path);
                return config;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt/locked config should not stop the launcher from starting with defaults.
        }
        return new LauncherConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>
    /// One-time move of legacy data (settings, steamcmd, server, backups, logs) from next to the exe
    /// into <see cref="DataRoot"/>, so an existing install keeps its server without re-downloading.
    /// Best-effort per item (a locked/in-use item is left in place and finished on a later run);
    /// returns the names actually moved, for logging.
    /// </summary>
    public static IReadOnlyList<string> MigrateLegacyLayout()
    {
        var moved = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        try { Directory.CreateDirectory(DataRoot); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return moved; }

        // Old name -> new name (the server + logs folders are also renamed for clarity).
        var items = new[]
        {
            ("launcher.json", "launcher.json"),
            ("steamcmd", "steamcmd"),
            ("backups", "backups"),
            ("server", ServerFolderName),
            ("logs", LogsFolderName),
        };
        foreach (var (oldName, newName) in items)
        {
            var src = Path.Combine(baseDir, oldName);
            var dst = Path.Combine(DataRoot, newName);
            if ((!Directory.Exists(src) && !File.Exists(src)) || Directory.Exists(dst) || File.Exists(dst))
                continue;
            try
            {
                if (Directory.Exists(src)) Directory.Move(src, dst);
                else File.Move(src, dst);
                moved.Add(newName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Locked/in-use (e.g. a running server), leave it; a later run finishes the move.
            }
        }
        return moved;
    }

    private static bool PathsEqual(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Rewrite <paramref name="path"/> with the fully-serialized config when it differs from what's on
    /// disk (missing new fields, a different formatter, etc.), so the file always reflects the current
    /// schema. Best-effort, a locked/read-only file just leaves the in-memory defaults in place.
    /// </summary>
    private static void NormalizeFileToCurrentSchema(LauncherConfig config, string rawOnDisk, string path)
    {
        try
        {
            var canonical = JsonSerializer.Serialize(config, JsonOptions);
            if (canonical != rawOnDisk)
                File.WriteAllText(path, canonical);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Non-fatal: the launcher still runs with the loaded (default-filled) config.
        }
    }
}

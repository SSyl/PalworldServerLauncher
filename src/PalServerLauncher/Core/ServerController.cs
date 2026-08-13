using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;
using PalServerLauncher.Rest.Models;
using PalServerLauncher.State;

namespace PalServerLauncher.Core;

/// <summary>
/// Owns the lifecycle of the managed Palworld server process. Stateless/re-attachable: on
/// <see cref="Attach"/> it adopts an already-running server (surviving a launcher restart), and
/// only launches when none is found. Hard crashes are caught via <see cref="Process.Exited"/>
/// (event-driven, no polling); graceful stops go save -> shutdown -> (force stop) -> kill.
/// </summary>
public sealed class ServerController : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly SteamCmd _steamCmd;
    private readonly Logger _logger;
    private readonly object _gate = new();

    private Process? _process;
    // Set on the launching thread, read by the stdout/stderr callbacks on the thread pool.
    private volatile string? _captureSecret;
    private HealthMonitor? _health;
    private UpdateMonitor? _updateMonitor;
    private readonly RestartScheduler _scheduler;
    private readonly BackupService _backup;
    private readonly BackupScheduler _backupScheduler;
    private readonly DiscordNotifier _discord;
    private readonly DiscordBotService _discordBot;
    private readonly LauncherIpcServer _ipc;
    private ServerState _lastNotifiedState = ServerState.Stopped;
    private readonly SemaphoreSlim _steamGate = new(1, 1); // serialize all SteamCMD runs - never two at once

    /// <summary>The build the pending update is chasing, or null for a plain start where nothing specific was
    /// aimed at. Consumed by the update that acts on it.</summary>
    private string? _targetBuildId;

    /// <summary>Read/write access to PalWorldSettings.ini game settings (used by the settings editor; gated to stopped).</summary>
    public GameSettingsService GameSettings { get; }

    /// <summary>Steam Workshop mod management (the Mods dialog scans / opens the folder through it; the launch
    /// path downloads + applies mods through it).</summary>
    public ModService ModService { get; }
    private readonly RestartBudget _restartBudget = new();
    private DateTime? _serverStartedUtc;
    private bool _sawRunning;
    private DateTime? _crashScanFromUtc;
    private bool _manualStop;
    private readonly RelaunchGate _relaunchGate = new(); // suppresses auto-recovery / restart relaunch after a deliberate stop, until the next user Start
    private readonly StopGate _stopGate = new();         // one stop ladder at a time; a second caller joins the running one
    private bool _restartInProgress;
    private bool _timedShutdownActive; // true while a timed shutdown's server-side countdown runs (drives the Shutdown Now affordance)
    private CancellationTokenSource? _restartCts; // cancels a pending broadcast countdown on a user Stop
    private bool _disposed;
    private ServerState _state = ServerState.Stopped;

    public ServerController(LauncherConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
        _steamCmd = new SteamCmd(config.ServerRoot);
        _backup = new BackupService(config, logger);
        _discord = new DiscordNotifier(config, logger);
        GameSettings = new GameSettingsService(config.ServerRoot, logger);
        ModService = new ModService(config.ServerRoot, logger);
        StateChanged += NotifyDiscordOnStateChange;

        _scheduler = new RestartScheduler(config, logger,
            isRunning: () => IsServerRunning,
            serverStartedUtc: () => _serverStartedUtc,
            announce: AnnounceScheduledRestartAsync,
            restartNow: () => RestartNowAsync(RestartReason.Scheduled));
        _scheduler.NextRestartTextChanged += t => NextRestartTextChanged?.Invoke(t);
        _scheduler.Start();

        _backupScheduler = new BackupScheduler(config, logger,
            isRunning: () => IsServerRunning,
            triggerBackup: () => _backup.BackupNowAsync(BackupReason.Scheduled, RestClient, IsRunning()));
        _backupScheduler.NextBackupTextChanged += t => NextBackupTextChanged?.Invoke(t);
        _backupScheduler.Start();

        _discordBot = new DiscordBotService(config, logger, new DiscordBotService.DiscordCommands(
            Status: DiscordStatusAsync,
            Players: DiscordPlayersAsync,
            Save: DiscordSaveAsync,
            Backup: DiscordBackupAsync,
            Restart: DiscordRestartAsync,
            Stop: DiscordStopAsync,
            Start: DiscordStartAsync,
            Update: DiscordUpdateCheckAsync,
            Announce: DiscordAnnounceAsync,
            Kick: DiscordKickAsync,
            Ban: DiscordBanAsync,
            Unban: DiscordUnbanAsync,
            ResolvePlayerName: ResolvePlayerDisplayNameAsync));
        if (config.DiscordBotEnabled)
            FireAndForget(_discordBot.StartAsync, "Discord bot start");

        // Let a second copy of the exe (--stop-server) ask US to stop, instead of killing the process behind our
        // back, which OnProcessExited would read as a crash and relaunch.
        _ipc = new LauncherIpcServer(LauncherIpc.PipeNameFor(LauncherConfig.DataRoot), HandleCliStopAsync, _logger.Info);
        _ipc.Start();
    }

    /// <summary>What a CLI stop resolves to, before anything is actually done.</summary>
    public enum CliStopVerdict
    {
        /// <summary>No server of ours is up at all. A scripted "make sure it's down" has nothing to do.</summary>
        NothingToStop,

        /// <summary>A server of ours is running but this launcher hasn't bound to it yet (startup adoption is
        /// still pending, possibly behind a modal prompt). We can't stop it through the normal path.</summary>
        NotAdopted,

        /// <summary>A graceful stop was asked for on a server we can't save. Force is its own flag.</summary>
        NeedsRestApi,

        /// <summary>Run the stop.</summary>
        Proceed,
    }

    /// <summary>
    /// Resolve a CLI stop from what the launcher can see. Pure, so the combinations that decide whether a script
    /// gets a success, a refusal, or an actual stop are unit-tested rather than inferred from the call site.
    /// </summary>
    public static CliStopVerdict ResolveCliStop(StopKind kind, bool adopted, bool anyServerRunning, bool restUsable)
    {
        if (!adopted)
            return anyServerRunning ? CliStopVerdict.NotAdopted : CliStopVerdict.NothingToStop;
        if (kind != StopKind.Kill && !restUsable)
            return CliStopVerdict.NeedsRestApi;
        return CliStopVerdict.Proceed;
    }

    /// <summary>
    /// Whether a verdict should latch the relaunch gate. Only for verdicts that mean "the server is meant to be
    /// down": an actual stop, and the nothing-bound case, which covers a restart sitting between its own stop and
    /// start (minutes wide, thanks to the startup backup, update, and mod sync) where nothing would otherwise
    /// suppress the restart's relaunch. A refusal must NOT latch, or a still-running server silently loses crash
    /// auto-restart and zombie recovery until the next Start.
    /// </summary>
    public static bool ShouldLatchRelaunchGate(CliStopVerdict verdict) =>
        verdict is CliStopVerdict.Proceed or CliStopVerdict.NothingToStop;

    /// <summary>Serve a stop asked for from the command line, through the same methods the Stop button uses so
    /// the relaunch latch is set and the exit is never mistaken for a crash. Runs on the pipe's thread.</summary>
    private async Task<StopOutcome> HandleCliStopAsync(StopRequest request, Action<string> report)
    {
        Process? process;
        lock (_gate)
            process = _process;

        var adopted = process is { HasExited: false };

        // Adoption happens later than the listener and can sit behind a modal prompt, so "no process bound"
        // does not mean "no server running", and reporting success would tell a script the server is down.
        using var unadopted = adopted ? null : ProcessScanner.FindManagedServer(_config.ServerRoot);

        var verdict = ResolveCliStop(request.Kind, adopted, anyServerRunning: unadopted is not null, RestClient is not null);

        if (ShouldLatchRelaunchGate(verdict))
        {
            lock (_gate)
            {
                _restartCts?.Cancel();
                _relaunchGate.SuppressForDeliberateStop();
            }
        }

        switch (verdict)
        {
            case CliStopVerdict.NothingToStop:
                return new StopOutcome(true, "No server is running.");

            case CliStopVerdict.NotAdopted:
                return new StopOutcome(false,
                    "A server is running but this launcher hasn't adopted it yet. Finish the launcher's startup prompt, then try again.");

            case CliStopVerdict.NeedsRestApi:
                return new StopOutcome(false,
                    "REST API is off, so the server can't be saved or shut down cleanly. Use --kill-server to force it down.");
        }

        var live = process!; // Proceed implies a bound, live process

        switch (request.Kind)
        {
            case StopKind.Kill:
                report("Killing the server process.");
                ForceShutdownNow();
                // Kill() is asynchronous, so confirm the exit landed before reporting the server down.
                return await WaitForExitAsync(live, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false)
                    ? new StopOutcome(true, "Server killed.")
                    : new StopOutcome(false, "The server process did not exit within 10s.");

            case StopKind.Countdown:
                // Joining sends no /shutdown of our own, so there is no countdown to report on and waiting would
                // just block for the other stop's duration.
                if (_stopGate.IsRunning)
                    return new StopOutcome(false, "A stop is already in progress, so the countdown was not started.");

                // Don't hold the pipe open for the countdown (up to an hour), but don't claim success before the
                // server has accepted it either. Answer on the server's own reply to /shutdown, which arrives
                // after the save and shutdown backup, then let the countdown run on without us.
                var requested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var countdown = ShutdownWithCountdownAsync(request.Seconds,
                    onShutdownRequested: accepted => requested.TrySetResult(accepted));
                FireAndForget(() => countdown, "CLI timed shutdown");

                report("Saving and requesting the shutdown...");
                // Whichever lands first: the server's answer, or the whole stop ending without one (no REST, the
                // process already gone, a failure). Only the first is a countdown that actually started.
                var settled = await Task.WhenAny(requested.Task, countdown).ConfigureAwait(false);
                // A refusal is NOT the end of it: the stop ladder keeps going and force stops the server shortly.
                // Say so, or a script reads the failure as "nothing happened" and the server goes down anyway.
                return settled == requested.Task && requested.Task.Result
                    ? new StopOutcome(true, $"Shutting down in {request.Seconds}s.")
                    : new StopOutcome(false,
                        "The server did not accept the shutdown request. The launcher will force stop it shortly if it doesn't exit on its own.");

            default:
                // A graceful stop can run for minutes (the shutdown backup zips SaveGames first), so mirror the
                // launcher's own log to the waiting CLI rather than leaving it staring at one line.
                void Forward(LogEntry entry)
                {
                    if (entry.Channel == LogChannel.General)
                        report(entry.Text);
                }

                _logger.LineForUi += Forward;
                try
                {
                    await StopAsync(graceful: true).ConfigureAwait(false);
                }
                finally
                {
                    _logger.LineForUi -= Forward;
                }
                return IsRunning()
                    ? new StopOutcome(false, "The server did not stop.")
                    : new StopOutcome(true, "Server stopped.");
        }
    }

    /// <summary>Reconnect the Discord bot after its settings change (called by the UI on Save).</summary>
    public void ApplyDiscordSettings() => FireAndForget(_discordBot.ReconfigureAsync, "Discord bot reconfigure");

    /// <summary>A short server-status line for the Discord /status command.</summary>
    private async Task<string> DiscordStatusAsync()
    {
        var state = State;
        if (!IsServerRunning)
            return $"### 🖥️ Server status\n**State:** {state}\n_The server isn't running._";
        var rest = RestClient;
        if (rest is null)
            return $"### 🖥️ Server status\n**State:** {state}\n_REST API off, no live stats._";

        var metrics = await rest.GetMetricsAsync().ConfigureAwait(false);
        if (metrics is null)
            return $"### 🖥️ Server status\n**State:** {state}\n_REST not responding._";

        var info = await rest.GetInfoAsync().ConfigureAwait(false);
        var uptime = TimeSpan.FromSeconds(metrics.Uptime);
        var uptimeText = uptime.TotalHours >= 1 ? $"{(int)uptime.TotalHours}h {uptime.Minutes}m" : $"{uptime.Minutes}m";
        var memory = CurrentServerMemory();

        return $"### 🖥️ Server status\n"
             + $"**State:** {state}\n"
             + $"**Version:** {info?.Version ?? "?"}\n"
             + $"**Players:** {metrics.CurrentPlayerNum} / {metrics.MaxPlayerNum}\n"
             + $"**FPS:** {metrics.ServerFps}\n"
             + $"**Frame time:** {metrics.ServerFrameTime:0.##} ms\n"
             + (memory is null ? "" : $"**Memory:** {memory}\n")
             + $"**Uptime:** {uptimeText}\n"
             + $"**In-game days:** {metrics.Days}\n"
             + $"**Base camps:** {metrics.BaseCampNum}";
    }

    /// <summary>The running server's current memory (working set), formatted like the status tile, or null if
    /// there's no process or it can't be read. Memory isn't a REST field, so this reads the OS process directly.</summary>
    private string? CurrentServerMemory()
    {
        var process = _process;
        if (process is null)
            return null;
        try
        {
            process.Refresh();
            return MemoryFormat.Format(process.WorkingSet64);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The online-player list for the Discord /players command.</summary>
    private async Task<string> DiscordPlayersAsync()
    {
        var rest = RestClient;
        if (rest is null)
            return "REST API is off, the player list is unavailable.";

        var players = await rest.GetPlayersAsync().ConfigureAwait(false);
        if (players is null)
            return "Couldn't read the player list (REST not responding).";
        if (players.Players.Count == 0)
            return "No players online.";

        var names = string.Join("\n", players.Players.Select(p => $"- {SanitizeName(p.Name ?? p.AccountName)}"));
        return $"**{players.Players.Count} online:**\n{names}";
    }

    /// <summary>Escape Discord markdown in an untrusted player name so it can't forge formatting, masked
    /// links, or extra lines in a message. Mentions are separately neutralized by the notifier and bot.</summary>
    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "?";
        var sb = new StringBuilder(name.Length + 4);
        foreach (var c in name)
        {
            if (c is '\r' or '\n')
                continue;
            if (c is '*' or '_' or '~' or '`' or '|' or '\\' or '<' or '>' or '[' or ']')
                sb.Append('\\');
            sb.Append(c);
        }
        return sb.Length == 0 ? "?" : sb.ToString();
    }

    private async Task<string> DiscordSaveAsync()
    {
        var rest = RestClient;
        if (rest is null)
            return "REST API is off, can't save.";
        return await rest.SaveAsync().ConfigureAwait(false) ? "World saved." : "The save request wasn't accepted.";
    }

    private async Task<string> DiscordBackupAsync()
    {
        var path = await _backup.BackupNowAsync(BackupReason.Manual, RestClient, IsRunning()).ConfigureAwait(false);
        return path is null ? "Backup failed, or there was nothing to back up." : $"Backup written: {Path.GetFileName(path)}";
    }

    private Task<string> DiscordRestartAsync()
    {
        if (!IsRunning())
            return Task.FromResult("Server isn't running.");
        FireAndForget(() => RestartAsync(RestartReason.Manual), "Discord restart");
        return Task.FromResult("Restarting the server now.");
    }

    private Task<string> DiscordStopAsync()
    {
        if (!IsRunning())
            return Task.FromResult("Server isn't running.");
        FireAndForget(() => StopAsync(graceful: true), "Discord stop");
        return Task.FromResult("Stopping the server...");
    }

    private Task<string> DiscordStartAsync()
    {
        if (IsRunning())
            return Task.FromResult("Server is already running.");
        if (!IsInstalled)
            return Task.FromResult("Server isn't installed, install it from the launcher first.");
        // Unattended start, can't prompt; warn like the headless path does.
        WarnIfWorldOptionPresent();
        WarnIfWorldMissing();
        FireAndForget(() => StartAsync(attended: false), "Discord start");
        return Task.FromResult("Starting the server (updating first if needed)...");
    }

    private async Task<string> DiscordUpdateCheckAsync()
    {
        var (result, latest) = await CheckForUpdateAsync().ConfigureAwait(false);
        return result switch
        {
            UpdateCheckResult.UpdateAvailable => $"Update available: build {latest}. Use /restart to apply it.",
            UpdateCheckResult.UpToDate => "Server is up to date.",
            _ => "Update check failed, see the launcher log.",
        };
    }

    private async Task<string> DiscordAnnounceAsync(string message)
    {
        if (RestClient is null)
            return "REST API is off, can't announce.";
        return await AnnounceAsync(message).ConfigureAwait(false) ? "Announcement sent." : "The announce request wasn't accepted.";
    }

    private async Task<string> DiscordKickAsync(string userId, string reason)
    {
        if (RestClient is null)
            return "REST API is off, can't kick.";
        // Resolve the name before kicking (they're still online); after the kick they'd be gone from /players.
        var who = await DescribeUserAsync(userId).ConfigureAwait(false);
        return await KickPlayerAsync(userId, reason).ConfigureAwait(false) ? $"Kicked {who}." : "Kick wasn't accepted (check the user id).";
    }

    private async Task<string> DiscordBanAsync(string userId, string reason)
    {
        if (RestClient is null)
            return "REST API is off, can't ban.";
        var who = await DescribeUserAsync(userId).ConfigureAwait(false);
        return await BanPlayerAsync(userId, reason).ConfigureAwait(false) ? $"Banned {who}." : "Ban wasn't accepted (check the user id).";
    }

    private async Task<string> DiscordUnbanAsync(string userId)
    {
        if (RestClient is null)
            return "REST API is off, can't unban.";
        // No name to resolve here: a banned player isn't online, so we can only echo the id.
        return await UnbanPlayerAsync(userId).ConfigureAwait(false) ? $"Unbanned `{userId}`." : "Unban wasn't accepted (check the user id).";
    }

    /// <summary>Resolve a platform user id to a Discord-safe (markdown-escaped) display name from the current
    /// player list, or null if the id isn't online / REST is off. Lets Discord kick/ban show who they hit.</summary>
    public async Task<string?> ResolvePlayerDisplayNameAsync(string userId)
    {
        var players = await GetPlayersAsync().ConfigureAwait(false);
        var player = players?.Players.FirstOrDefault(p => string.Equals(p.UserId, userId, StringComparison.OrdinalIgnoreCase));
        var name = player?.Name ?? player?.AccountName;
        return string.IsNullOrWhiteSpace(name) ? null : SanitizeName(name);
    }

    /// <summary>"**Name** (`userid`)" for a known online player, else "`userid`", for Discord result messages.</summary>
    private async Task<string> DescribeUserAsync(string userId)
    {
        var name = await ResolvePlayerDisplayNameAsync(userId).ConfigureAwait(false);
        return name is null ? $"`{userId}`" : $"**{name}** (`{userId}`)";
    }

    public event Action<ServerState>? StateChanged;
    public event Action<HealthSample>? HealthUpdated;
    public event Action<string>? NextRestartTextChanged;
    public event Action<string>? NextBackupTextChanged;
    public event Action<string>? UpdateStatusChanged;

    /// <summary>Asked when a SteamCMD run the user started fails. Set by the view model, which routes it to a
    /// dialog. Null outside the GUI.</summary>
    public Func<ViewModels.SteamCmdFailure, ViewModels.SteamCmdFailureChoice>? ConfirmSteamCmdFailure { get; set; }
    /// <summary>A timed shutdown's countdown began (the total seconds) or ended (null), for the mirror countdown / Shutdown Now button.</summary>
    public event Action<int?>? TimedShutdownChanged;

    /// <summary>True if a managed server process is currently running (used by the close/startup prompts).</summary>
    public bool IsServerRunning => IsRunning();

    /// <summary>How many managed server instances were found under our root at the last <see cref="Attach"/>.</summary>
    public int RunningInstanceCount { get; private set; }

    public ServerState State
    {
        get { lock (_gate) return _state; }
        private set
        {
            ServerState previous;
            // Compare-and-set under the lock so concurrent writers (OnProcessExited, the health monitor,
            // restart/recovery) can't lose a store. Fire the event OUTSIDE the lock, handlers must not be
            // run while holding _gate (they marshal to the UI / post to Discord and could otherwise deadlock).
            lock (_gate)
            {
                if (_state == value) return;
                previous = _state;
                _state = value;
                // Any "up" state clears the startup claim. Deliberately generous: only the "during startup"
                // wording asserts anything, so a state that over-reports booting costs a vaguer message,
                // while one that under-reports would call a 6-hour-old server's crash a startup failure.
                if (value is ServerState.Healthy or ServerState.Degraded or ServerState.Zombie or ServerState.RestUnreachable)
                    _sawRunning = true;
            }
            _logger.Debug($"State: {previous} -> {value}");
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>The managed process, or null when stopped. Exposed so the health monitor can read memory / I/O counters.</summary>
    public Process? Process => _process;

    /// <summary>REST client built from the current PalWorldSettings.ini, or null if the API isn't usable.</summary>
    public PalworldRestClient? RestClient { get; private set; }

    private string PalWorldSettingsPath => PalWorldSettingsPathFor(_config.ServerRoot);

    /// <summary>The game's settings ini under a given server root. Static so the headless CLI stop path can read
    /// the REST credentials without building a controller.</summary>
    public static string PalWorldSettingsPathFor(string serverRoot) => Path.Combine(
        LauncherConfig.ServerDir(serverRoot), "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");

    private string SaveGamesDir => Path.Combine(
        LauncherConfig.ServerDir(_config.ServerRoot), "Pal", "Saved", "SaveGames");

    /// <summary>
    /// Every <c>WorldOption.sav</c> under the save folder (usually one, empty on a fresh install with no saves
    /// yet). Present when a save was converted from a local/co-op world, this file overrides
    /// PalWorldSettings.ini on a dedicated server and can leave the launcher unable to reach REST. Copies inside
    /// Palworld's nested rolling "backup" folders are skipped.
    /// </summary>
    public IReadOnlyList<string> FindWorldOptionSavs()
    {
        if (!Directory.Exists(SaveGamesDir))
            return Array.Empty<string>();
        return Directory.EnumerateFiles(SaveGamesDir, WorldOptionSav.FileName, SearchOption.AllDirectories)
            .Where(path => !BackupService.ShouldSkipEntry(Path.GetRelativePath(SaveGamesDir, path)))
            .ToList();
    }

    /// <summary>
    /// Rename a <c>WorldOption.sav</c> to a non-clobbering <c>.bak</c> so the server reads PalWorldSettings.ini
    /// instead. Returns false with <paramref name="error"/> set (and nothing renamed) on an IO/permission failure.
    /// </summary>
    public bool TryRenameWorldOptionSav(string path, out string bakPath, out string? error)
    {
        bakPath = WorldOptionSav.BakTargetPath(path, File.Exists);
        error = null;
        try
        {
            File.Move(path, bakPath);
            _logger.Info($"WorldOption.sav renamed to {Path.GetFileName(bakPath)} in {Path.GetDirectoryName(path)}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            _logger.Error($"Couldn't rename WorldOption.sav: {ex.Message}");
            return false;
        }
    }

    /// <summary>Log a warning if a WorldOption.sav is present, for unattended start paths (headless start,
    /// Discord /start) that can't show the interactive prompt. It overrides PalWorldSettings.ini and can
    /// disable the REST API the launcher needs.</summary>
    public void WarnIfWorldOptionPresent()
    {
        if (FindWorldOptionSavs().Count > 0)
            _logger.Info("WorldOption.sav found in the save folder. It can override PalWorldSettings.ini and leave the server uncontrollable. Rename it to .bak, or start the launcher normally to be prompted.");
    }

    private string ConfigDir => Path.Combine(
        LauncherConfig.ServerDir(_config.ServerRoot), "Pal", "Saved", "Config", "WindowsServer");

    private string GameUserSettingsPath => Path.Combine(ConfigDir, GameUserSettingsFile.FileName);

    /// <summary>The world folders on disk, i.e. the directory names under <c>SaveGames\0</c> (the dedicated
    /// server's only slot). Empty when nothing has been saved yet. A folder counts only once it holds a
    /// <c>Level.sav</c>, so a leftover or half-copied directory can't be offered up as someone's world.</summary>
    public IReadOnlyList<string> FindWorldIds()
    {
        var slotDir = Path.Combine(SaveGamesDir, "0");
        if (!Directory.Exists(slotDir))
            return Array.Empty<string>();
        try
        {
            return Directory.EnumerateDirectories(slotDir)
                .Where(dir => File.Exists(Path.Combine(dir, "Level.sav")))
                .Select(Path.GetFileName).OfType<string>().ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Couldn't list the save folders: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Whether <c>DedicatedServerName</c> still names a world that's actually on disk. When it doesn't,
    /// the server silently starts a brand new world and leaves the real save untouched but unreachable, which is
    /// what a restored backup, an imported save, or a hand-copied save folder all look like.</summary>
    public WorldSelectionVerdict CheckWorldSelection() =>
        WorldSelection.Evaluate(ReadConfiguredWorldId(), FindWorldIds());

    private string? ReadConfiguredWorldId()
    {
        try
        {
            return File.Exists(GameUserSettingsPath)
                ? GameUserSettingsFile.ReadWorldId(File.ReadAllText(GameUserSettingsPath))
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Couldn't read {GameUserSettingsFile.FileName}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Point <c>GameUserSettings.ini</c> at <paramref name="worldId"/>, leaving every other line alone.
    /// The write is read back and re-parsed before we report success, the same way a settings save is verified.
    /// Returns false with <paramref name="error"/> set on an IO/permission failure.</summary>
    public bool TryLoadWorld(string worldId, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var existing = File.Exists(GameUserSettingsPath) ? File.ReadAllText(GameUserSettingsPath) : "";
            File.WriteAllText(GameUserSettingsPath, GameUserSettingsFile.SetWorldId(existing, worldId));

            var written = GameUserSettingsFile.ReadWorldId(File.ReadAllText(GameUserSettingsPath));
            if (!string.Equals(written, worldId, StringComparison.OrdinalIgnoreCase))
            {
                error = $"{GameUserSettingsFile.FileName} still reads {written ?? "nothing"} after the write.";
                _logger.Error(error);
                return false;
            }

            _logger.Info($"Server world set to {worldId} in {GameUserSettingsFile.FileName}.");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            _logger.Error($"Couldn't update {GameUserSettingsFile.FileName}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Log the world-selection state for unattended start paths (headless start, Discord /start) that
    /// can't prompt. Never rewrites the config on its own: clearing the key is also how a user deliberately
    /// starts a fresh world, so the choice stays theirs.</summary>
    public void WarnIfWorldMissing()
    {
        var verdict = CheckWorldSelection();
        if (verdict.State == WorldSelectionState.Recoverable)
            _logger.Info($"The server isn't pointed at the save on disk ({verdict.WorldId}), so it will start a NEW empty world. Start the launcher normally to be prompted, or set DedicatedServerName in {GameUserSettingsFile.FileName}.");
        else if (verdict.State == WorldSelectionState.Ambiguous)
            _logger.Info($"The server isn't pointed at any of the {FindWorldIds().Count} saves on disk, so it will start a NEW empty world. Set DedicatedServerName in {GameUserSettingsFile.FileName} to the world you want.");
    }

    /// <summary>Running Palworld servers this launcher doesn't manage (a foreign install, or one whose path we
    /// can't read). Starting while one runs risks a port conflict or a competing duplicate.</summary>
    public IReadOnlyList<ProcessScanner.UnmanagedServer> FindUnmanagedServers() =>
        ProcessScanner.FindUnmanagedServers(_config.ServerRoot);

    /// <summary>Terminate a server process by pid (for the unmanaged-server prompt). Logs the outcome, returns
    /// false with <paramref name="error"/> set if it can't (e.g. it's running elevated).</summary>
    public bool TryTerminateServer(int pid, out string? error)
    {
        var result = ProcessScanner.TryTerminate(pid, out error);
        var report = ProcessScanner.DescribeTerminate(result, pid, error);
        if (report.IsError)
            _logger.Error(report.LogMessage);
        else
            _logger.Info(report.LogMessage);
        return report.Succeeded;
    }

    /// <summary>
    /// Scan for an already-running managed server WITHOUT adopting it, so the UI can prompt (reconnect / shut
    /// down / exit) before the launcher binds and starts monitoring. Sets <see cref="RunningInstanceCount"/>.
    /// Call <see cref="Attach"/> afterwards to actually adopt (on reconnect or before a shut-down).
    /// </summary>
    public int DetectRunningInstances()
    {
        var all = ProcessScanner.FindAllManagedServers(_config.ServerRoot);
        RunningInstanceCount = all.Count;
        foreach (var proc in all)
            proc.Dispose(); // detect only: don't hold handles, Attach re-scans if the user reconnects
        if (all.Count == 0)
        {
            _logger.Debug("Startup scan: no running Palworld server found under the server root.");
            State = ServerState.Stopped;
        }
        else
        {
            _logger.Debug($"Startup scan: {all.Count} running server instance(s) detected, waiting for the user's choice before adopting.");
        }
        return all.Count;
    }

    /// <summary>
    /// Adopt an already-running managed server (bind it, start monitoring, build the REST client), so the
    /// launcher can control it. Called AFTER the startup prompt, on reconnect or before a shut-down, never
    /// before the user has chosen. Returns true if one was found and adopted.
    /// </summary>
    public bool Attach()
    {
        var all = ProcessScanner.FindAllManagedServers(_config.ServerRoot);
        RunningInstanceCount = all.Count;

        if (all.Count == 0)
        {
            _logger.Debug("Startup scan: no running Palworld server found under the server root.");
            State = ServerState.Stopped;
            return false;
        }

        var pidList = string.Join(", ", all.Select(p => "PID " + p.Id));
        var existing = all[0];
        for (var i = 1; i < all.Count; i++)
            all[i].Dispose(); // adopt only the primary; extras are handled by StopAllInstancesAsync

        lock (_gate)
        {
            _manualStop = false;
            BindProcess(existing, adopted: true);
            RebuildRestClient();
        }
        _logger.Info(all.Count == 1
            ? $"Startup scan: detected 1 running server ({pidList}), adopted so it can be controlled."
            : $"Startup scan: detected {all.Count} running server instances ({pidList}), adopted PID {existing.Id}; use Shut Down All to stop them.");
        // Stdout can only be redirected at process start, so an adopted server's output is gone for good.
        // Without this the two empty tabs read as the capture being broken.
        _logger.Info("Server was already running at launcher start, so its output cannot be captured. " +
                     "The Server and Chat tabs stay empty until it is restarted from here.");
        State = ServerState.Starting; // health monitor promotes to Healthy once REST responds
        return true;
    }

    /// <summary>True when the server binary exists on disk.</summary>
    public bool IsInstalled => File.Exists(ProcessScanner.ExpectedExePath(_config.ServerRoot));

    /// <summary>The build id currently installed on disk (from the SteamCMD app manifest), or null if not installed.
    /// Read when the version pin is enabled to capture the build being frozen.</summary>
    public string? InstalledBuildId => _steamCmd.ReadInstalledBuildId();

    /// <summary>The cached game version (e.g. v1.0.0) known for <paramref name="buildId"/>, or null if none is
    /// cached for that exact build. REST only reports the version while the server runs, so this lets the pinned
    /// caption and update status show the friendly version even when stopped. A build change invalidates the
    /// cache (the stored build no longer matches), so it falls back to build-only until REST reports again.</summary>
    public string? KnownVersionFor(string? buildId) =>
        !string.IsNullOrEmpty(buildId)
        && string.Equals(_config.LastKnownVersionBuild, buildId, StringComparison.Ordinal)
        && _config.LastKnownVersion.Length > 0
            ? _config.LastKnownVersion : null;

    /// <summary>A build's display label: "v1.0.1 (24181105)" when the version is known for it, else the localized
    /// "build 24181105". Used for the pinned caption and the pinned status line.</summary>
    public string BuildDisplay(string? buildId) =>
        VersionFormat.Label(KnownVersionFor(buildId), buildId, Strings.Main_PinnedBuildFormat);

    /// <summary>Remember the version REST reported for the installed build, so it can be shown while stopped.
    /// Ignores the health sample's non-version sentinels ("-", "REST off") and only writes on a real change.
    /// Runs on the HealthMonitor's background thread, so it must never throw: a failed persist must not fault
    /// the health loop (that would silently stop monitoring / auto-recovery), so everything is guarded.</summary>
    private void CacheVersion(string rawVersion)
    {
        try
        {
            var shortVersion = VersionFormat.ShortVersion(rawVersion);
            if (shortVersion is null)
                return;
            var build = _steamCmd.ReadInstalledBuildId();
            if (string.IsNullOrEmpty(build))
                return;
            if (_config.LastKnownVersion == shortVersion && _config.LastKnownVersionBuild == build)
                return;
            _config.LastKnownVersion = shortVersion;
            _config.LastKnownVersionBuild = build;
            _config.Save();
            // The version just became known, so refresh the pinned / updates-off status line to swap build-only
            // for "version (build)". Only fires the one time a given build is first seen (cache is a no-op after).
            RefreshUpdateStatusText();
        }
        catch (Exception ex)
        {
            _logger.Debug($"Version cache update skipped: {ex.Message}");
        }
    }

    /// <summary>True when PalWorldSettings.ini has the REST API enabled with a non-blank admin password.</summary>
    public bool IsRestApiConfigured => IniReader.ReadFile(PalWorldSettingsPath).RestApiUsable;

    /// <summary>A read-only snapshot of the REST / RCON / port values from PalWorldSettings.ini (for the port checker).</summary>
    public PalworldServerSettings ReadServerSettings() => IniReader.ReadFile(PalWorldSettingsPath);

    /// <summary>
    /// Enable the REST API in PalWorldSettings.ini with a fresh cryptographically-random admin password
    /// (seeding the ini from the default template if needed). Stopped-only, the settings service refuses
    /// while the server runs. Returns false if it couldn't be written (not installed, or running).
    /// </summary>
    public bool EnableRestApiWithRandomPassword()
    {
        if (!GameSettings.EnsureInitialized())
            return false;
        return GameSettings.Save(new Dictionary<string, string>
        {
            ["RESTAPIEnabled"] = "True",
            ["AdminPassword"] = GenerateAdminPassword(),
        }, IsServerRunning);
    }

    /// <summary>A 20-char alphanumeric password from a CSPRNG, deliberately not derivable from time/source.</summary>
    public static string GenerateAdminPassword() =>
        RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 20);

    /// <summary>Install the server for the first time. Validates.</summary>
    public Task InstallAsync(CancellationToken ct = default) =>
        RunSteamCmdAsync(SteamCmdOperation.Install, ct);

    /// <summary>Check the installed files against Steam's and repair what fails. The Validate Files button.</summary>
    public Task ValidateFilesAsync(CancellationToken ct = default) =>
        RunSteamCmdAsync(SteamCmdOperation.Validate, ct);

    /// <summary>Apply a build already known to be newer. Skips validation, since the update overwrites anyway.</summary>
    public Task DownloadUpdateAsync(CancellationToken ct = default) =>
        RunSteamCmdAsync(SteamCmdOperation.Update, ct);

    /// <summary>
    /// Install, validate or update the server via SteamCMD. Always user-triggered, never reached from
    /// <see cref="StartAsync"/>, so a plain Start can't surprise anyone with a multi-GB download. Refuses
    /// while the server runs, since its files are locked.
    ///
    /// Private so callers go through the three named entry points above. app_update is the same command for
    /// all of them, so a wrong operation in an argument list reads like working code. Validate Files reported
    /// "couldn't update the server" for a commit that way.
    /// </summary>
    private async Task RunSteamCmdAsync(SteamCmdOperation requested, CancellationToken ct = default)
    {
        if (IsRunning())
        {
            _logger.Info("Stop the server before installing or updating.");
            return;
        }

        _logger.Info(_config.HideSteamCmdWindow
            ? "Installing / updating server via SteamCMD (live log in the SteamCMD tab)..."
            : "Installing / updating server via SteamCMD (a console window will open; live log in the SteamCMD tab)...");

        // Read before the run: a part-failed first install can still answer IsInstalled afterward.
        var operation = ResolveOperation(requested, IsInstalled);
        var validate = ValidatesFiles(operation);
        var target = _targetBuildId;
        _targetBuildId = null;
        var steamLog = new Progress<string>(line => _logger.SteamCmd(line));
        // Mirror SteamCMD's own console log into the SteamCMD tab while it runs.
        using var tail = new FileTailer(_steamCmd.ConsoleLogPath, _logger.SteamCmd, fromStart: false);
        // SteamCMD appends to its logs across runs, so anything read back afterward is filtered to this one.
        // Second resolution, matching its timestamps, and a second early rather than late.
        var runStarted = DateTime.Now.AddSeconds(-1);

        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct).ConfigureAwait(false);
            var exit = await _steamCmd.InstallOrUpdateServerAsync(
                validate: validate, visible: !_config.HideSteamCmdWindow, steamLog, ct).ConfigureAwait(false);

            var buildId = _steamCmd.ReadInstalledBuildId() ?? "?";
            if (exit == 0 && ReachedTarget(target, buildId))
            {
                RecordUpdateOutcome(failedBuild: null);
                _logger.Info($"Install/update complete (build {buildId}).");
                return;
            }

            _logger.Error(exit == 0
                ? $"SteamCMD reported success, but the server is on build {buildId}, not {target}."
                : $"SteamCMD couldn't {OperationVerb(operation)} the server (exit code {exit}).");
            if (target is not null)
                RecordUpdateOutcome(failedBuild: target);

            // Built before the retry so it describes this run rather than the retry's, see UpdateInPlaceAsync.
            var failure = BuildSteamCmdFailure(operation, exit, runStarted, launchPending: false);
            // A first install has no build to report against, and the tile is about updates.
            if (operation != SteamCmdOperation.Install)
                UpdateStatusChanged?.Invoke(string.Format(Strings.Update_Failed, BuildDisplay(buildId)));

            if (AskAboutSteamCmdFailure(failure, userInitiated: true) == ViewModels.SteamCmdFailureChoice.ValidateAndRetry
                && (await RepairInstallAndUpdateAsync(target, steamLog, ct).ConfigureAwait(false)).Succeeded)
                RecordUpdateOutcome(failedBuild: null);
        }
        finally
        {
            _steamGate.Release();
        }
    }

    /// <summary>
    /// Copy an existing (non-launcher) server install from <paramref name="sourceDir"/> into the managed folder,
    /// leaving the original in place. Refuses if the server is running, a managed server already exists, the
    /// source doesn't look like a real install, or the source contains the destination. The caller offers REST
    /// setup afterwards (like a fresh install). Returns true on a completed copy.
    /// </summary>
    public async Task<bool> ImportServerAsync(string sourceDir, CancellationToken ct = default)
    {
        if (IsRunning())
        {
            _logger.Info("Stop the server before importing.");
            return false;
        }
        if (IsInstalled)
        {
            _logger.Info("A server is already installed here. Remove it before importing another.");
            return false;
        }
        if (!ServerImporter.LooksLikeServerInstall(sourceDir))
        {
            _logger.Info("That folder doesn't look like a Palworld dedicated server install (no server exe found).");
            return false;
        }

        var dest = _steamCmd.InstallDir;
        if (ProcessScanner.IsUnder(dest, sourceDir) || PathsEqual(sourceDir, dest))
        {
            _logger.Info("Can't import a folder into itself. Pick the existing install, not the launcher's own server folder.");
            return false;
        }

        _logger.Info($"Importing server from {sourceDir} into {dest} (the original is left in place)...");
        var progress = new Progress<string>(_logger.Info);
        try
        {
            await ServerImporter.CopyDirectoryAsync(sourceDir, dest, progress, ct).ConfigureAwait(false);
            _steamCmd.InvalidateBuildIdCache(); // the imported install brings its own manifest / build id
            _logger.Info($"Import complete (build {_steamCmd.ReadInstalledBuildId() ?? "?"}). Once you've confirmed this copy works, you can delete the original yourself.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Import cancelled.");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("Import failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Bring a Linux server across: install the Windows server with SteamCMD, then copy the world and the settings
    /// out of the Linux install (its own binaries can't run here). The original is left in place. The settings move
    /// between the platform-named config folders, which is the step that makes this more than a file copy.
    /// Returns true once the Windows server is installed and the world is in, false if anything stopped it.
    /// </summary>
    public async Task<bool> ImportLinuxServerAsync(string sourceDir, CancellationToken ct = default)
    {
        if (IsRunning())
        {
            _logger.Info("Stop the server before importing.");
            return false;
        }
        if (IsInstalled)
        {
            _logger.Info("A server is already installed here. Remove it before importing another.");
            return false;
        }
        if (ServerImporter.DetectInstallKind(sourceDir) != ServerInstallKind.Linux)
        {
            _logger.Info("That folder doesn't look like a Linux Palworld dedicated server install (no PalServer.sh found).");
            return false;
        }

        var dest = _steamCmd.InstallDir;
        if (ProcessScanner.IsUnder(dest, sourceDir) || PathsEqual(sourceDir, dest))
        {
            _logger.Info("Can't import a folder into itself. Pick the existing install, not the launcher's own server folder.");
            return false;
        }

        // A half-copied world would be read by the pre-Start guard as "the save on disk" and offered up as the one
        // to load, so a failed copy has to leave nothing behind. Only ours to delete if we're the ones who made it.
        var destSaves = Path.Combine(dest, ServerImporter.SaveGamesRelative);
        var savesExistedBefore = Directory.Exists(destSaves);
        var progress = new Progress<string>(_logger.Info);
        try
        {
            _logger.Info($"Importing a Linux server from {sourceDir}. Installing the Windows server first, its Linux binaries can't run here.");
            await InstallAsync(ct).ConfigureAwait(false);
            if (!IsInstalled)
            {
                _logger.Error("Import stopped: the Windows server didn't install, so there's nothing to copy the world into. Check the SteamCMD tab.");
                return false;
            }

            var payload = await ServerImporter.CopyWorldAndSettingsAsync(
                sourceDir, dest, ServerImporter.LinuxConfigRelative, progress, ct).ConfigureAwait(false);

            if (!payload.Saves)
                _logger.Info("No save folder found in the Linux install, so the server will start a new world.");
            foreach (var name in ServerImporter.PortableConfigFiles)
            {
                if (!payload.ConfigFiles.Contains(name))
                    _logger.Info($"No {name} in the Linux install's config folder, so the server's own default is used.");
            }

            _logger.Info("Import complete. Once you've confirmed this copy works, you can delete the original yourself.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.Info("Import cancelled.");
            DiscardPartialImport(destSaves, savesExistedBefore);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error("Import failed", ex);
            DiscardPartialImport(destSaves, savesExistedBefore);
            return false;
        }
    }

    /// <summary>Remove a world copied in by an import that then failed, so the install is left as a plain fresh one
    /// rather than holding a truncated save. Never touches saves that were already there.</summary>
    private void DiscardPartialImport(string destSaves, bool savesExistedBefore)
    {
        if (savesExistedBefore || !Directory.Exists(destSaves))
            return;
        try
        {
            Directory.Delete(destSaves, recursive: true);
            _logger.Info("Removed the partially copied world, so the install is a clean one. Import it again to retry.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Couldn't remove the partially copied world at {destSaves}: {ex.Message}. Delete that folder before importing again.");
        }
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

    /// <summary>Outcome of a read-only manual update check (the "Check for Update" button).</summary>
    public enum UpdateCheckResult
    {
        UpToDate,
        UpdateAvailable,
        CheckFailed,
        /// <summary>Steam's app manifest is missing or unreadable, so there's no installed build to compare
        /// against. Distinct from UpToDate, which this used to be reported as.</summary>
        InstalledBuildUnknown,
    }

    /// <summary>
    /// Read-only manual update check: compares the installed build id to the latest published one via
    /// SteamCMD WITHOUT downloading anything. The caller decides whether to then run the update. Reuses
    /// the same gated build-id query + comparison the background <see cref="UpdateMonitor"/> uses.
    /// </summary>
    public async Task<(UpdateCheckResult Result, string? LatestBuildId)> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var installed = _steamCmd.ReadInstalledBuildId();
        UpdateStatusChanged?.Invoke(Strings.Update_Checking);
        var latest = await QueryLatestBuildIdGatedAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(latest))
        {
            UpdateStatusChanged?.Invoke(string.Format(Strings.Update_CheckFailed, BuildDisplay(installed)));
            return (UpdateCheckResult.CheckFailed, null);
        }
        // Without a readable manifest there's nothing to compare, and reporting that as up to date is a lie
        // that hides the real problem. Validate rebuilds the manifest from the files already on disk.
        if (string.IsNullOrWhiteSpace(installed))
        {
            _logger.Info("Steam's app manifest is missing or unreadable. Installed build unknown.");
            UpdateStatusChanged?.Invoke(Strings.Update_BuildUnknown);
            return (UpdateCheckResult.InstalledBuildUnknown, latest);
        }
        if (UpdateMonitor.IsUpdateAvailable(installed, latest))
        {
            // Whatever the user does next is aimed at this build, so the update that follows can tell whether it
            // landed. Deliberately IsUpdateAvailable rather than ShouldOfferUpdate: a build being held after a
            // failed attempt is exactly what someone pressing Check for Update is asking to try again.
            _targetBuildId = latest.Trim();
            UpdateStatusChanged?.Invoke(string.Format(Strings.Update_Available, latest));
            return (UpdateCheckResult.UpdateAvailable, latest);
        }
        UpdateStatusChanged?.Invoke(string.Format(Strings.Update_UpToDate, BuildDisplay(installed)));
        return (UpdateCheckResult.UpToDate, latest);
    }

    /// <summary>
    /// Update-then-launch (the Start button). Runs SteamCMD app_update first (when <see
    /// cref="LauncherConfig.UpdateOnStart"/> is on, or <paramref name="forceUpdate"/> for an explicit
    /// update-restart) so the server is current on boot, then launches. A failed/offline update doesn't
    /// block launch, we run the installed build. A missing install routes to Install instead (never a
    /// surprise multi-GB download). <paramref name="userInitiated"/> is true for a user Start (it clears a
    /// prior deliberate-stop relaunch suppression) and false when called from a restart (a Stop or Force
    /// Shutdown during the restart stays in effect, so the restart's own relaunch is suppressed and the server
    /// stays down until a user Start).
    /// </summary>
    public async Task StartAsync(bool forceUpdate = false, bool userInitiated = true, CancellationToken ct = default, bool? attended = null)
    {
        if (IsRunning())
        {
            _logger.Info("Start ignored, server already running.");
            return;
        }

        if (!IsInstalled)
        {
            _logger.Info("Server not installed. Click Install / Update first.");
            return;
        }

        // A user Start clears a prior deliberate-stop suppression and refills the auto-restart budget. A restart
        // passes false so a Stop or Force Shutdown during the restart keeps the server down instead of being
        // undone by the restart's relaunch, and so an automatic restart can't refill its own budget.
        lock (_gate)
        {
            _relaunchGate.OnStart(userInitiated);
            _restartBudget.OnStart(userInitiated);
        }

        // Back up before the update, in case SteamCMD ever wipes PalWorldSettings.ini.
        if (_config.BackupOnStartup)
            await _backup.BackupNowAsync(BackupReason.Startup, rest: null, serverRunning: false, ct).ConfigureAwait(false);

        // The per-start update respects the version pin (blocks all updates) and the Update-on-start toggle. An
        // explicit update-restart (forceUpdate) overrides Update-on-start being off but never the pin. See UpdatePolicy.
        if (UpdatePolicy.ShouldUpdateBeforeLaunch(forceUpdate, _config.VersionPinEnabled, _config.UpdateOnStart))
        {
            // A plain Start is attended by definition. A restart has to say so explicitly.
            if (!await UpdateInPlaceAsync(attended ?? userInitiated, ct).ConfigureAwait(false))
            {
                _logger.Info("Start cancelled. Server was not updated.");
                State = ServerState.Stopped;
                return;
            }
        }
        else
        {
            var skipReason = _config.VersionPinEnabled
                ? $"version pinned to build {(_config.PinnedBuildId.Length > 0 ? _config.PinnedBuildId : "current")}"
                : "Update on start is off";
            _logger.Info($"Skipping the start-time update ({skipReason}).");
        }

        // Download + enable mods (or reconcile them off) so this boot reflects the current mod config. A
        // restart routes through here too, so it re-syncs. A failed sync never blocks the launch.
        await SyncModsAsync(ct).ConfigureAwait(false);

        await LaunchServerAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Take a backup on demand (the "Backup now" button). Fresh <c>/save</c> if running + REST usable.</summary>
    public Task BackupNowAsync(CancellationToken ct = default) =>
        _backup.BackupNowAsync(BackupReason.Manual, RestClient, IsRunning(), ct);

    // --- Live server command surface (shared by the Server Commands dialog and the Discord bot). All return
    // false / null when the REST API is off or unreachable, so callers report "couldn't do it" rather than crash. ---

    /// <summary>Online players from the REST API, or null if REST is off / unreachable.</summary>
    public Task<PlayersResponse?> GetPlayersAsync(CancellationToken ct = default) =>
        RestClient?.GetPlayersAsync(ct) ?? Task.FromResult<PlayersResponse?>(null);

    // Each command logs its outcome to the Server Log (visible in the Server Log tab, alongside join/leave),
    // since the game server doesn't echo REST commands to its own output and the dialog / Discord otherwise
    // leave no trace there. Both the dialog and the Discord bot route through here, so one log site covers both.

    /// <summary>Broadcast an in-game message to everyone on the server.</summary>
    public async Task<bool> AnnounceAsync(string message, CancellationToken ct = default)
    {
        if (RestClient is not { } rest)
            return false;
        var ok = await rest.AnnounceAsync(message, ct).ConfigureAwait(false);
        _logger.Server(ok ? $"Broadcast: {message}" : "Broadcast rejected by the server.");
        return ok;
    }

    /// <summary>Kick a player by their platform user id, with an optional reason.</summary>
    public async Task<bool> KickPlayerAsync(string userId, string message, CancellationToken ct = default)
    {
        if (RestClient is not { } rest)
            return false;
        var ok = await rest.KickAsync(userId, message, ct).ConfigureAwait(false);
        _logger.Server(ok ? $"Kicked {userId}.{Reason(message)}" : $"Kick rejected for {userId}.");
        return ok;
    }

    /// <summary>Ban a player by their platform user id, with an optional reason.</summary>
    public async Task<bool> BanPlayerAsync(string userId, string message, CancellationToken ct = default)
    {
        if (RestClient is not { } rest)
            return false;
        var ok = await rest.BanAsync(userId, message, ct).ConfigureAwait(false);
        _logger.Server(ok ? $"Banned {userId}.{Reason(message)}" : $"Ban rejected for {userId}.");
        return ok;
    }

    /// <summary>Lift a ban on a player by their platform user id.</summary>
    public async Task<bool> UnbanPlayerAsync(string userId, CancellationToken ct = default)
    {
        if (RestClient is not { } rest)
            return false;
        var ok = await rest.UnbanAsync(userId, ct).ConfigureAwait(false);
        _logger.Server(ok ? $"Unbanned {userId}." : $"Unban rejected for {userId}.");
        return ok;
    }

    /// <summary>Trigger a fresh world save.</summary>
    public async Task<bool> SaveWorldAsync(CancellationToken ct = default)
    {
        if (RestClient is not { } rest)
            return false;
        var ok = await rest.SaveAsync(ct).ConfigureAwait(false);
        _logger.Server(ok ? "World saved." : "Save rejected by the server.");
        return ok;
    }

    /// <summary>" Reason: X" for a non-empty kick/ban reason, else empty.</summary>
    private static string Reason(string message) => string.IsNullOrWhiteSpace(message) ? "" : $" Reason: {message}";

    /// <summary>Graceful shutdown with an in-game countdown and no relaunch. Routes through the stop ladder so
    /// the resulting exit is treated as a deliberate stop, not a crash. Cancels any pending restart countdown
    /// first (like <see cref="StopAsync"/>), so a deliberate shutdown can't be undone by a restart relaunch.</summary>
    /// <param name="onShutdownRequested">Signalled with the server's answer to the /shutdown call, as soon as it
    /// answers. Lets a caller that will not wait out the countdown (the CLI) still report whether the countdown
    /// actually started.</param>
    public Task ShutdownWithCountdownAsync(int seconds, CancellationToken ct = default, Action<bool>? onShutdownRequested = null)
    {
        lock (_gate)
        {
            _restartCts?.Cancel();
            _relaunchGate.SuppressForDeliberateStop(); // a deliberate shutdown stays stopped, like a plain Stop
        }
        return StopCoreAsync(graceful: true, shutdownWaitSeconds: seconds, restarting: false, ct, onShutdownRequested);
    }

    /// <summary>Accelerate a timed shutdown that's counting down: send a fresh REST /shutdown(1), which overrides the
    /// pending timer (Palworld honors the latest /shutdown). The in-flight <see cref="StopCoreAsync"/> wait catches
    /// the resulting exit and clears the mirror. No-ops if no timed shutdown is counting down or REST is off.</summary>
    public async Task<bool> ShutdownNowAsync()
    {
        PalworldRestClient? rest;
        lock (_gate)
        {
            if (!_timedShutdownActive)
                return false;
            rest = RestClient;
        }
        if (rest is null)
            return false;
        var ok = await rest.ShutdownAsync(1, "Server is shutting down now.").ConfigureAwait(false);
        _logger.Server(ok ? "Shutdown accelerated to now." : "Shutdown-now request was rejected.");
        return ok;
    }

    /// <summary>
    /// Immediately kill the server process (direct OS kill, no REST, no save). The escape hatch for a wedged
    /// server or a graceful stop that's dragging, usable whenever the process is alive. A DIRECT kill, not a
    /// second stop-ladder, so it can't race an in-progress stop, and killing the process also unblocks a stuck
    /// graceful shutdown. Sets manual-stop so the exit isn't read as a crash, and cancels any pending restart.
    /// </summary>
    public void ForceShutdownNow()
    {
        Process? process;
        HealthMonitor? health;
        UpdateMonitor? updateMonitor;
        lock (_gate)
        {
            _manualStop = true;
            // Latch so an auto-recovery already in flight (its stop phase can run for ~45s against a dead
            // REST API) doesn't relaunch the server we're about to kill. Cleared by the next explicit Start.
            _relaunchGate.SuppressForDeliberateStop();
            _restartCts?.Cancel();
            process = _process;
            // Detach the monitors under the lock (consistent with StopCoreAsync) so the kill isn't mistaken
            // for a zombie; dispose the locals below.
            health = _health;
            _health = null;
            updateMonitor = _updateMonitor;
            _updateMonitor = null;
        }

        health?.Dispose();
        updateMonitor?.Dispose();

        if (process is null || process.HasExited)
        {
            State = ServerState.Stopped;
            return;
        }

        _logger.Info("Force stop requested, stopping the server process now.");
        KillNow(process);
    }

    /// <summary>Recompute the next-restart/next-backup UI text immediately (after a schedule setting change).</summary>
    public void RefreshScheduleText()
    {
        _scheduler.Refresh();
        _backupScheduler.Refresh();
    }

    /// <summary>Push the update-status tile text immediately when the pin or update toggles change, so a pinned
    /// or updates-off state shows at once instead of waiting for the next server bind or monitor tick. When
    /// auto-updating and stopped, it clears any stale pinned/off text (e.g. after unpinning), so the tile does
    /// not keep showing "Pinned to..." once unpinned. While running and auto-updating the running monitor owns
    /// the text, so nothing is emitted (it would clobber the live "Up to date..." line).</summary>
    public void RefreshUpdateStatusText()
    {
        if (!IsInstalled)
            return;
        var installed = _steamCmd.ReadInstalledBuildId() ?? "?";
        if (_config.VersionPinEnabled)
            UpdateStatusChanged?.Invoke(string.Format(Strings.Update_Pinned, BuildDisplay(_config.PinnedBuildId.Length > 0 ? _config.PinnedBuildId : installed)));
        else if (!_config.AutoUpdateEnabled)
            UpdateStatusChanged?.Invoke(string.Format(Strings.Update_AutoUpdateOff, BuildDisplay(installed)));
        else if (!IsRunning())
            UpdateStatusChanged?.Invoke("-");
    }

    /// <summary>Why the last run failed, from SteamCMD's content log. The console we capture only says
    /// "state is 0x... after update job", so issue #15's reporter had to go find this file himself.</summary>
    private IReadOnlyList<string> ReadSteamCmdFailureReasons(DateTime since)
    {
        try
        {
            return SteamCmd.ExtractFailureReasons(File.ReadLines(_steamCmd.ContentLogPath), SteamCmd.AppId, since);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Record and log what a failed run said. SteamCMD appends to both logs across runs, so this must happen
    /// before anything else runs or it describes the wrong attempt. The disk is measured separately, see
    /// <see cref="WithCurrentDiskState"/>.
    /// </summary>
    private ViewModels.SteamCmdFailure BuildSteamCmdFailure(
        SteamCmdOperation operation, int exit, DateTime since, bool launchPending, bool log = true)
    {
        var state = ReadUpdateState(since);
        var reasons = ReadSteamCmdFailureReasons(since);
        var reachedApp = UpdateJobRan(since);

        if (log)
        {
            foreach (var reason in reasons)
                _logger.Error($"SteamCMD: {reason}");
            if (state is not null)
                _logger.Info($"SteamCMD exited with state {state}. Update did not apply.");

            // Nothing in the content log names the server when SteamCMD stopped before reaching it, so the
            // failure would otherwise be one bare exit code. No dialog is shown for this except after a repair
            // the user asked for, see AskAboutSteamCmdFailure.
            if (!reachedApp)
                _logger.Error("SteamCMD stopped before checking the server. Its output is in the SteamCMD tab.");
        }

        return new ViewModels.SteamCmdFailure(
            operation, exit, SteamCmd.ClassifyUpdateState(state), state,
            SteamCmd.StateChangedFiles(state), reachedApp, launchPending, reasons,
            RepairSpaceGb: null, FreeSpace: null);
    }

    /// <summary>
    /// Fill in the disk answers, measured now rather than when the failure was recorded: a repair can stage
    /// several GB onto the same drive in between. Checked on every failure rather than when a state suggests
    /// it, since it is the one thing we can settle ourselves.
    /// </summary>
    private ViewModels.SteamCmdFailure WithCurrentDiskState(ViewModels.SteamCmdFailure failure)
    {
        var bytes = RememberServerSize();
        var free = _steamCmd.FreeSpaceOnInstallDrive();

        // Measured against the install size, not the compressed download: SteamCMD stages into
        // steamapps/downloading before committing, so a full repair wants room for the install again on top of
        // the one already there. A failed run took the folder from 5.9 GB to 11 GB.
        var problem = failure.Problem;
        if (!_steamCmd.InstallDirIsWritable())
            problem = SteamCmdProblem.DiskWrite;
        else if (free is { } f && bytes > 0 && f < bytes)
            problem = SteamCmdProblem.DiskSpace;

        return failure with
        {
            Problem = problem,
            RepairSpaceGb = bytes > 0 ? bytes / (1024.0 * 1024 * 1024) : null,
            FreeSpace = free is { } bytesFree ? FormatBytes(bytesFree) : null,
        };
    }

    /// <summary>
    /// Ask what to do about a failure already described. Only for a run the user started, and normally only
    /// once SteamCMD reached the server: before that a modal would block the launch over a transient Steam
    /// outage, so it goes to the log instead. <paramref name="answering"/> overrides that for the ask
    /// following a repair the user requested, where they are waiting on an answer.
    /// </summary>
    private ViewModels.SteamCmdFailureChoice AskAboutSteamCmdFailure(
        ViewModels.SteamCmdFailure failure, bool userInitiated, bool answering = false) =>
        userInitiated && (failure.ReachedApp || answering) && ConfirmSteamCmdFailure is { } confirm
            ? confirm(WithCurrentDiskState(failure))
            : ViewModels.SteamCmdFailureChoice.Leave;

    private static string FormatBytes(long bytes) =>
        bytes >= 1024L * 1024 * 1024
            ? $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"
            : $"{bytes / (1024.0 * 1024):0} MB";

    private string? ReadUpdateState(DateTime since)
    {
        try
        {
            return SteamCmd.ExtractUpdateState(File.ReadLines(_steamCmd.ConsoleLogPath), SteamCmd.AppId, since);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>True when SteamCMD reached Steam and the app update itself failed, which is the only case the
    /// repair can fix. Offline runs fail before any update job and are left alone.</summary>
    private bool UpdateJobRan(DateTime since)
    {
        try
        {
            return SteamCmd.RanUpdateJob(File.ReadLines(_steamCmd.ConsoleLogPath), SteamCmd.AppId, since);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Debug($"Couldn't remove {path}: {ex.Message}");
        }
    }

    /// <summary>The server's install size in bytes, cached in config as a side effect. A repair moves the app
    /// manifest this comes from, so without the cache the second offer can never state a size.</summary>
    private long RememberServerSize()
    {
        if (_steamCmd.ReadInstalledSizeOnDisk() is not { } bytes || bytes <= 0)
            return _config.LastKnownServerSizeBytes;

        if (bytes != _config.LastKnownServerSizeBytes)
        {
            _config.LastKnownServerSizeBytes = bytes;
            _config.Save();
        }
        return bytes;
    }

    /// <summary>
    /// Set Steam's record of what's installed aside and update again with validation, so SteamCMD resolves
    /// the app from scratch instead of trusting a manifest it won't act on. Measured with the manifest absent:
    /// validate rebuilt it and fetched nothing in 13 seconds, where the same run without validate re-downloads
    /// 4.9 GB. Prompted rather than automatic, since a damaged install can still pull the lot. Runs inside the
    /// caller's <see cref="_steamGate"/> hold, so it must not take it.
    /// </summary>
    private async Task<(bool Succeeded, int Exit, DateTime Started)> RepairInstallAndUpdateAsync(
        string? target, IProgress<string> steamLog, CancellationToken ct)
    {
        var repairStarted = DateTime.Now.AddSeconds(-1);
        // Moved aside rather than deleted, so a retry that fails costs the user nothing.
        var manifest = _steamCmd.AppManifestPath;
        var stashed = manifest + ".bak";
        // An absent manifest is what a redownload produces, not a reason to refuse one. Issue #15's reporter
        // deleted his by hand, which is exactly when he needed this.
        var stashedIt = File.Exists(manifest);
        if (stashedIt)
        {
            try
            {
                File.Move(manifest, stashed, overwrite: true);
                _logger.Info("Set the Steam app manifest aside. Validating and updating.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error($"Couldn't move the Steam app manifest at {manifest}", ex);
                return (false, 0, repairStarted);
            }
        }
        else
            _logger.Info("No Steam app manifest to set aside. Validating and updating.");

        var succeeded = false;
        var exit = 0;
        try
        {
            exit = await _steamCmd.InstallOrUpdateServerAsync(
                validate: true, visible: !_config.HideSteamCmdWindow, steamLog, ct).ConfigureAwait(false);
            var buildId = _steamCmd.ReadInstalledBuildId() ?? "?";
            // Exit 0 is not enough: SteamCMD reports success having installed the build we were already on.
            succeeded = exit == 0 && ReachedTarget(target, buildId);
            if (succeeded)
            {
                _logger.Info($"Repair succeeded (build {buildId}).");
                UpdateStatusChanged?.Invoke(string.Format(Strings.Update_UpToDate, BuildDisplay(buildId)));
                return (true, exit, repairStarted);
            }

            _logger.Error(exit == 0
                ? $"Repair reported success, but the server is on build {buildId}, not {target}."
                : $"SteamCMD exited with code {exit} during repair.");
            foreach (var reason in ReadSteamCmdFailureReasons(repairStarted))
                _logger.Error($"SteamCMD: {reason}");
            // Logged here rather than left to the rebuilt failure record, which passes log: false to avoid
            // repeating the reasons above. The state is the token worth searching for and is not a duplicate.
            if (ReadUpdateState(repairStarted) is { } repairState)
                _logger.Info($"SteamCMD exited with state {repairState}. Update did not apply.");
            return (false, exit, repairStarted);
        }
        finally
        {
            // Also on cancel or throw, which would otherwise strand the manifest at .bak. Keyed on usable
            // rather than on-target: validating rebuilds the manifest, so a run can leave a good one while
            // settling on the wrong build.
            if (stashedIt)
                RestoreManifestUnlessUsable(manifest, stashed);
        }
    }

    /// <summary>Put the stashed manifest back unless the run left a readable one. A part-failed run writes a
    /// manifest with buildid 0 (measured), which reads as null, so the file existing is not enough to discard
    /// the original. Getting this wrong leaves the install with no build id and update checking dead.</summary>
    private void RestoreManifestUnlessUsable(string manifest, string stashed)
    {
        // The manifest was moved aside, so anything there now is one this run wrote. Readable is enough.
        if (_steamCmd.ReadInstalledBuildId() is not null)
        {
            TryDelete(stashed);
            return;
        }

        try
        {
            File.Move(stashed, manifest, overwrite: true);
            _steamCmd.InvalidateBuildIdCache();
            _logger.Info("Put the Steam app manifest back.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"Couldn't restore the Steam app manifest, it is at {stashed}", ex);
        }
    }

    /// <summary>Run SteamCMD app_update in place before launch (the "always current on boot" step). False when
    /// the user answered a failed update with Cancel, which stops the start rather than launching the old build.</summary>
    private async Task<bool> UpdateInPlaceAsync(bool userInitiated, CancellationToken ct)
    {
        _logger.Info("Checking for a server update before launch...");
        var steamLog = new Progress<string>(_logger.SteamCmd);
        using var tail = new FileTailer(_steamCmd.ConsoleLogPath, _logger.SteamCmd, fromStart: false);
        // SteamCMD appends to its logs across runs, so anything read back afterward is filtered to this one.
        // Second resolution, matching its timestamps, and a second early rather than late.
        var runStarted = DateTime.Now.AddSeconds(-1);
        // Consumed here: a later plain start aims at nothing and must not be judged against a stale target.
        var target = _targetBuildId;
        _targetBuildId = null;

        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Self-heal a missing SteamCMD (e.g. an imported server with no steamcmd/ folder) before updating.
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct, visible: !_config.HideSteamCmdWindow).ConfigureAwait(false);
            var exit = await _steamCmd.InstallOrUpdateServerAsync(
                validate: _config.VerifyOnUpdate, visible: !_config.HideSteamCmdWindow, steamLog, ct).ConfigureAwait(false);
            var buildId = _steamCmd.ReadInstalledBuildId() ?? "?";

            // Exit 0 doesn't mean it landed: a stale appinfo.vdf has SteamCMD apply nothing and report success,
            // which is a silent version of the same loop. Judge on the build id.
            if (exit == 0 && ReachedTarget(target, buildId))
            {
                RecordUpdateOutcome(failedBuild: null);
                _logger.Info($"Server up to date (build {buildId}).");
                UpdateStatusChanged?.Invoke(string.Format(Strings.Update_UpToDate, BuildDisplay(buildId)));
            }
            else
            {
                // "Couldn't verify or update" rather than "the update failed": app_update runs on every start
                // and usually has nothing to apply, so this means the build is unconfirmed, not out of date.
                var installed = buildId == "?" ? "the installed build" : $"the installed build (build {buildId})";
                _logger.Error(exit == 0
                    ? $"SteamCMD reported success, but the server is on {installed}, not {target}."
                    : $"SteamCMD couldn't verify or update the server (exit code {exit}).");
                if (target is not null)
                    RecordUpdateOutcome(failedBuild: target);
                UpdateStatusChanged?.Invoke(string.Format(Strings.Update_Failed, BuildDisplay(buildId)));

                // A known target means a specific newer build was being applied. Without one, app_update was
                // only confirming the server is current.
                // Built before the repair, which appends to both logs and would otherwise be what gets read.
                var operation = target is null ? SteamCmdOperation.VerifyOrUpdate : SteamCmdOperation.Update;
                var failure = BuildSteamCmdFailure(operation, exit, runStarted, launchPending: true);

                // Asked while the server is still stopped, the only cheap moment to redownload it. Never taken
                // automatically: clearing the manifest costs a measured 4.9 GB.
                var choice = AskAboutSteamCmdFailure(failure, userInitiated);
                if (choice == ViewModels.SteamCmdFailureChoice.ValidateAndRetry)
                {
                    var repair = await RepairInstallAndUpdateAsync(target, steamLog, ct).ConfigureAwait(false);
                    if (repair.Succeeded)
                    {
                        RecordUpdateOutcome(failedBuild: null);
                        return true;
                    }
                    // The repair was the fix on offer. Launching now would act on a choice the user didn't
                    // make, so ask again with it spent. Rebuilt from the repair's own window, since that is
                    // the run this dialog is about. Already logged by the repair, so not logged twice.
                    choice = AskAboutSteamCmdFailure(
                        BuildSteamCmdFailure(operation, repair.Exit, repair.Started, launchPending: true, log: false)
                            with { RepairFailed = true },
                        userInitiated, answering: true);
                }

                if (choice == ViewModels.SteamCmdFailureChoice.Cancel)
                    return false;
            }
        }
        finally
        {
            _steamGate.Release();
        }
        return true;
    }

    /// <summary>What SteamCMD is actually being asked to do. app_update is the same command for every button,
    /// so the caller's intent is the only source, and nothing can be verified or updated before a first install
    /// exists.</summary>
    public static SteamCmdOperation ResolveOperation(SteamCmdOperation requested, bool isInstalled) =>
        isInstalled ? requested : SteamCmdOperation.Install;

    /// <summary>Whether the run passes validate. Only applying a build already known to be newer skips it,
    /// since there is nothing to check that the update is not about to overwrite.</summary>
    public static bool ValidatesFiles(SteamCmdOperation operation) => operation != SteamCmdOperation.Update;

    private static string OperationVerb(SteamCmdOperation operation) => operation switch
    {
        SteamCmdOperation.Install => "install",
        SteamCmdOperation.Validate => "verify",
        _ => "update",
    };

    /// <summary>
    /// Whether the install reached the build the attempt was aiming at. No target means the exit code is all
    /// there is. Landing PAST it counts: a target can be minutes old, and Steam publishing a newer build in
    /// between is a success.
    /// </summary>
    public static bool ReachedTarget(string? target, string installed) =>
        string.IsNullOrWhiteSpace(target)
        || string.Equals(target, installed, StringComparison.Ordinal)
        || (long.TryParse(target, out var wanted) && long.TryParse(installed, out var got) && got > wanted);

    /// <summary>Remember the build an update failed to reach so the monitor stops offering it, or clear it once
    /// one lands. Only written when it changes, since this runs on every start.</summary>
    private void RecordUpdateOutcome(string? failedBuild)
    {
        var value = failedBuild ?? "";
        if (_config.FailedUpdateBuildId == value)
            return;

        _config.FailedUpdateBuildId = value;
        _config.Save();
    }

    /// <summary>
    /// Bring the server's mods in line with config just before launch (a restart routes through StartAsync, so
    /// it re-syncs). Mods on: download each enabled Workshop id with the connected Steam account, copy it into the
    /// server's Mods\Workshop only when its cache content or Force state changed (the update-detection gate),
    /// apply Force Server Install where set, resolve each PackageName, then write PalModSettings.ini enabling every
    /// enabled mod (downloaded + dropped-in). Mods off: turn the ini's master flag off if a previous run left it
    /// on. A failed sync never blocks launch, it logs and continues, same posture as the update step. SteamCMD
    /// work runs under <see cref="_steamGate"/>.
    /// </summary>
    private async Task SyncModsAsync(CancellationToken ct)
    {
        try
        {
            if (!_config.ModsEnabled)
            {
                // Unchecking "Enable mods" should take effect on the next start, so turn the ini master flag off
                // if a previous run enabled it. Don't create the file on a never-modded install.
                if (ModService.AreModsEnabledInIni())
                {
                    ModService.ApplyPalModSettings(globalEnable: false, Array.Empty<string>());
                    _logger.Info("Mods are off, disabled them in PalModSettings.ini.");
                }
                return;
            }

            var enabled = _config.Mods.Where(m => m.Enabled).ToList();
            var toDownload = enabled.Where(m => !string.IsNullOrWhiteSpace(m.WorkshopId)).ToList();

            if (toDownload.Count > 0 && string.IsNullOrWhiteSpace(_config.SteamUsername))
                _logger.Info("Mods are on but no Steam account is connected, skipping Workshop downloads. Dropped-in mods still apply. Connect an account in the Mods dialog.");
            else if (toDownload.Count > 0)
                await DownloadModsAsync(toDownload, ct).ConfigureAwait(false);

            // ActiveModList = enabled mods that actually deploy server-side: a mod whose Info.json declares
            // IsServer (a real server mod, or one we inject here via Force). A client-only, un-forced mod is left
            // out, so the server writes it `modname : 0` and UE4SS won't load any leftover files. The Force
            // injection is a local file edit (no Steam needed), applied here every start and idempotent, so it
            // holds even when there was no download this start (no account, or an unchanged mod).
            var active = new List<string>();
            foreach (var mod in enabled)
            {
                var folder = !string.IsNullOrWhiteSpace(mod.WorkshopId) ? mod.WorkshopId : mod.FolderName;
                var info = ModService.GetModInfo(folder);
                if (info is null || string.IsNullOrWhiteSpace(info.PackageName))
                {
                    _logger.Info($"Mod '{ModDisplayName(mod)}' has no readable Info.json yet, it won't activate until it's downloaded or scanned.");
                    continue;
                }
                mod.PackageName = info.PackageName; // keep the cached name fresh for the dialog

                var serverDeployable = info.IsServer;
                if (mod.ForceServerInstall && !string.IsNullOrWhiteSpace(mod.WorkshopId))
                    serverDeployable |= ApplyForceServer(mod, info.PackageName);

                if (serverDeployable)
                    active.Add(info.PackageName);
                else
                    _logger.Info($"Mod '{ModDisplayName(mod)}' isn't marked to run on dedicated servers (no IsServer). Leaving it out of the active list. Tick Force to run it anyway.");
            }
            ModService.ApplyPalModSettings(globalEnable: true, active);
        }
        catch (Exception ex)
        {
            _logger.Error("Mod sync failed, launching without applying mod changes", ex);
        }
    }

    /// <summary>Download each enabled Workshop mod under the SteamCMD gate, then copy it into the server's
    /// Mods\Workshop only when its cache content or Force state changed since the last sync (the update-detection
    /// gate, so a large unchanged mod isn't re-copied on every start), applying Force Server Install where set.
    /// The per-mod sync signature persists in mod-sync.json. Stops early on an auth failure so the user is told to
    /// reconnect once, not once per mod. State is saved even on an early exit.</summary>
    private async Task DownloadModsAsync(IReadOnlyList<ModEntry> mods, CancellationToken ct)
    {
        var steamLog = new Progress<string>(_logger.SteamCmd);
        var statePath = ModSyncState.PathFor(_config.ServerRoot);
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        var state = ModSyncState.Load(statePath); // load + save inside the gate, so the state file has one writer
        try
        {
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct).ConfigureAwait(false);
            foreach (var mod in mods)
            {
                var result = await _steamCmd.DownloadWorkshopItemAsync(_config.SteamUsername, mod.WorkshopId, steamLog, ct).ConfigureAwait(false);
                if (result == SteamCmd.WorkshopDownloadResult.AuthFailed)
                {
                    _logger.Error("Steam sign-in expired, reconnect your account in the Mods dialog. Skipping the remaining downloads.");
                    break;
                }
                if (result != SteamCmd.WorkshopDownloadResult.Ok)
                    continue; // a single failed download shouldn't stop the others

                try { SyncDownloadedMod(mod, state); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // One mod's copy/force failure shouldn't stop the others or skip the ini write.
                    _logger.Error($"Couldn't sync mod {ModDisplayName(mod)}, skipping it", ex);
                }
            }
        }
        finally
        {
            state.Save(statePath);
            _steamGate.Release();
        }
    }

    /// <summary>Copy one just-downloaded mod into Mods\Workshop only when its cache content (manifest) or Force
    /// state changed since the last sync, and record the new sync signature. Skipping that copy when nothing
    /// changed is what avoids re-copying a large unchanged mod every start. PackageName resolution and the Force
    /// injection happen afterward while building the active list, so they apply even without a download this
    /// start (e.g. no Steam account connected).</summary>
    private void SyncDownloadedMod(ModEntry mod, ModSyncState state)
    {
        var liveState = _steamCmd.ReadWorkshopItemState(mod.WorkshopId);
        var liveManifest = liveState?.Manifest ?? "";
        if (liveState is not null)
            mod.TimeUpdated = liveState.TimeUpdated;

        var folderPresent = Directory.Exists(Path.Combine(ModService.WorkshopDir, mod.WorkshopId));
        state.Items.TryGetValue(mod.WorkshopId, out var recorded);
        if (!ModSyncState.NeedsSync(recorded, liveManifest, mod.ForceServerInstall, folderPresent))
            return;

        // Mirror a clean copy from SteamCMD's cache (replaces any prior Force injection with the author's file).
        // Only record the sync signature if the copy actually landed, so a missing source is retried next start
        // instead of being pinned as up-to-date.
        if (ModService.CopyDownloadedMod(mod.WorkshopId, _steamCmd.WorkshopContentDir(mod.WorkshopId)))
            state.Items[mod.WorkshopId] = new ModSyncEntry { Manifest = liveManifest, Forced = mod.ForceServerInstall };
    }

    /// <summary>Inject IsServer:true into a forced mod's source Info.json, and on a real change clear the deployed
    /// manifest so the server redeploys it with the flag. Idempotent (returns AlreadyServer and writes nothing
    /// when it's already injected). Returns whether the mod is server-deployable after the pass (Forced or
    /// AlreadyServer), so the caller can decide ActiveModList membership.</summary>
    private bool ApplyForceServer(ModEntry mod, string? pkg)
    {
        switch (ModService.ForceServerFlag(mod.WorkshopId))
        {
            case ForceOutcome.Forced:
                _logger.Info($"Forcing mod {ModDisplayName(mod)} to run on server.");
                if (!string.IsNullOrWhiteSpace(pkg))
                    ClearDeployed(pkg!);
                return true;
            case ForceOutcome.AlreadyServer:
                _logger.Info($"Mod {ModDisplayName(mod)} already has IsServer: True. Nothing to change.");
                return true;
            default: // NotApplicable
                _logger.Info($"Couldn't force mod {ModDisplayName(mod)} to run on the server: no InstallRule found in its Info.json.");
                return false;
        }
    }

    /// <summary>Clear a mod's deployed manifest, logging a failed delete instead of letting it break the sync.</summary>
    private void ClearDeployed(string packageName)
    {
        try { ModService.ClearDeployedMod(packageName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Info($"Couldn't clear the deployed manifest for {packageName}: {ex.Message}");
        }
    }

    /// <summary>Swap a mod's source Info.json back to the author's original from SteamCMD's Workshop cache (removing
    /// our injected IsServer), for when it's un-forced. No-op (logged) if the cache copy is gone or the copy fails,
    /// so un-force still proceeds regardless (the mod leaves ActiveModList either way). Called from the Mods dialog:
    /// runs fire-and-forget under the SteamCMD gate so the copy can't overlap a sync's mod-folder copy, and never
    /// blocks the UI Save. The un-force disables the mod, so it won't run regardless of when this lands, this is
    /// just on-disk tidiness, so eventual completion is fine.</summary>
    public void RestoreOriginalModInfo(string workshopId)
    {
        if (string.IsNullOrWhiteSpace(workshopId))
            return;
        FireAndForget(async () =>
        {
            await _steamGate.WaitAsync().ConfigureAwait(false);
            try { RestoreOriginalModInfoCore(workshopId); }
            finally { _steamGate.Release(); }
        }, "restore original mod Info.json");
    }

    private void RestoreOriginalModInfoCore(string workshopId)
    {
        var original = Path.Combine(_steamCmd.WorkshopContentDir(workshopId), "Info.json");
        var dest = Path.Combine(ModService.WorkshopDir, workshopId, "Info.json");
        try
        {
            if (File.Exists(original) && Directory.Exists(Path.GetDirectoryName(dest)!))
            {
                File.Copy(original, dest, overwrite: true);
                _logger.Info($"Restored the original Info.json for mod {workshopId} after un-forcing it.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Info($"Couldn't restore the original Info.json for mod {workshopId}: {ex.Message}");
        }
    }

    /// <summary>A friendly identifier for a mod in a log line: its name, else its Workshop id, else "(local mod)".</summary>
    private static string ModDisplayName(ModEntry mod) =>
        !string.IsNullOrWhiteSpace(mod.ModName) ? mod.ModName
        : !string.IsNullOrWhiteSpace(mod.WorkshopId) ? mod.WorkshopId
        : "(local mod)";

    /// <summary>
    /// Run SteamCMD's interactive one-time sign-in so it caches a session for Workshop downloads. The launcher
    /// collects the password and passes it straight to SteamCMD (never storing or logging it), sparing the user
    /// SteamCMD's blank password prompt; its visible window still handles Steam Guard (phone approval or an echoed
    /// code). Under the gate.
    /// </summary>
    public async Task<bool> ConnectSteamAsync(string username, string password, CancellationToken ct = default)
    {
        var steamLog = new Progress<string>(_logger.SteamCmd);
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct).ConfigureAwait(false);
            await _steamCmd.ConnectAccountAsync(username, password, steamLog, ct).ConfigureAwait(false);
            // The sign-in window's exit code isn't SteamCMD's, so confirm the session took with a quick hidden check.
            var connected = await _steamCmd.HasCachedSessionAsync(username, ct).ConfigureAwait(false);
            _logger.Info(connected
                ? $"Steam account '{username}' connected."
                : $"Steam sign-in for '{username}' didn't complete, try again.");
            return connected;
        }
        finally
        {
            _steamGate.Release();
        }
    }

    /// <summary>Check whether SteamCMD still has a usable session for the account, without opening a login window
    /// (a hidden captured login under the gate). False if the account is blank or SteamCMD isn't installed. Lets
    /// the Mods dialog show a verified sign-in status on open.</summary>
    public async Task<bool> CheckSteamLoginAsync(string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(username) || !File.Exists(_steamCmd.SteamCmdExe))
            return false;
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _steamCmd.HasCachedSessionAsync(username, ct).ConfigureAwait(false);
        }
        finally
        {
            _steamGate.Release();
        }
    }

    /// <summary>Launch the installed server (no update). Used by Start (after updating), crash-restart, and recovery.</summary>
    private Task LaunchServerAsync(CancellationToken ct = default)
    {
        if (IsRunning())
            return Task.CompletedTask;

        // The REST API being disabled is not fatal, we still launch (a fresh install has no
        // PalWorldSettings.ini until the server generates one on first boot). We just lose
        // stats/health/graceful-shutdown until the user enables it.
        var settings = IniReader.ReadFile(PalWorldSettingsPath);
        // Set before the handlers are wired, so the first captured line is already redacted.
        _captureSecret = settings.AdminPassword;
        if (!settings.RestApiUsable)
        {
            _logger.Info(File.Exists(PalWorldSettingsPath)
                ? "REST API not enabled, starting without stats/health. Set AdminPassword + RESTAPIEnabled=True in PalWorldSettings.ini for full control."
                : "First run, the server will generate its config. Afterward, set AdminPassword + RESTAPIEnabled=True for stats/health, then restart.");
        }

        var exe = ProcessScanner.ExpectedExePath(_config.ServerRoot);
        // An explicit query port is used as-is (the user forwards it). 0 keeps the auto-pick of the first free
        // UDP port from 27015, so several servers on one box don't collide.
        var queryPort = _config.QueryPort > 0 ? _config.QueryPort : FindFreeUdpPort(27015);
        var args = BuildLaunchArgs(_config, queryPort);

        // Launched hidden (the launcher owns the server, no stray console window). We capture the
        // server's stdout/stderr and mirror it into the Server Log tab.
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // Route captured server output (see LogServerOutput): drop noise (blank lines and every "REST accessed
        // endpoint" echo of our own calls), send in-game chat to the Chat log, and the rest to the Server Log.
        process.OutputDataReceived += (_, e) => LogServerOutput(e.Data);
        process.ErrorDataReceived += (_, e) => LogServerOutput(e.Data);
        lock (_gate)
        {
            // Authoritative check inside the lock: if another launch (crash-relaunch / recovery /
            // restart) won the race and already started a server, drop this one, never double-launch.
            // Also drop it when a deliberate stop / force shutdown latched, so an in-flight auto-recovery or
            // restart can't relaunch the server the user just stopped (cleared by the next user Start).
            if (!_relaunchGate.MayLaunch(IsRunningNoLock()))
            {
                process.Dispose();
                if (_relaunchGate.Suppressed)
                    _logger.Info("Launch skipped, the server was deliberately stopped. Click Start to run it again.");
                return Task.CompletedTask;
            }
            _manualStop = false;
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            BindProcess(process, adopted: false);
            RebuildRestClient();
        }
        _logger.Info($"Server launched (PID {process.Id}, queryport {queryPort}).");
        State = ServerState.Starting;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stop the server (the plain Stop button / close prompts). Graceful = save (plus the shutdown backup
    /// if enabled), then an immediate in-game shutdown, with force-stop/kill fallback. Stop means stop now:
    /// staged player warnings are the Restart button's job, not a plain Stop. Also cancels any pending
    /// restart countdown so a Stop during a broadcast really stops.
    /// </summary>
    public async Task StopAsync(bool graceful = true, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _restartCts?.Cancel();
            _relaunchGate.SuppressForDeliberateStop(); // a user Stop stays stopped: don't let a racing recovery / restart relaunch it
        }
        // 0 -> StopCoreAsync clamps to the 1s minimum /shutdown requires, i.e. an immediate shutdown.
        await StopCoreAsync(graceful, shutdownWaitSeconds: 0, restarting: false, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Stop the adopted server gracefully, then force-stop any other managed instances under our root
    /// (orphans/duplicates we can't reach over REST). Used when startup finds more than one running.
    /// </summary>
    public async Task StopAllInstancesAsync(CancellationToken ct = default)
    {
        await StopAsync(graceful: true, ct).ConfigureAwait(false);

        foreach (var proc in ProcessScanner.FindAllManagedServers(_config.ServerRoot))
        {
            try
            {
                if (!proc.HasExited)
                {
                    _logger.Info($"Force-stopping extra server instance (PID {proc.Id}).");
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _logger.Info($"Could not stop an extra instance: {ex.Message}");
            }
            finally
            {
                proc.Dispose();
            }
        }
        RunningInstanceCount = 0;
    }

    /// <summary>Clamp a requested shutdown countdown to Palworld's minimum: POST /shutdown rejects waittime=0
    /// with a 400, so it is always at least 1s. The requested value is otherwise honored as-is, an explicit
    /// timed shutdown must not be shortened just because the server happens to be empty. Pure, so it's tested.</summary>
    public static int ShutdownWaitSeconds(int requested) => Math.Max(1, requested);

    /// <summary>Whether a captured server output line is worth showing in the Server Log. Drops nulls (the
    /// end-of-stream marker), blank lines (the server emits one after each REST access), and every "REST accessed
    /// endpoint" line (the launcher's own polls and commands, all noise). Ordinary server output is kept.</summary>
    public static bool ShouldLogServerLine(string? line) =>
        !string.IsNullOrWhiteSpace(line) && !IsRestAccessLogLine(line);

    /// <summary>True for the server's own "REST accessed endpoint" access line. The launcher drives ALL REST
    /// traffic itself (the health polls plus commands), so these just echo our own calls back and flood the
    /// Server Log. We drop them all; command outcomes are still logged by the launcher's own command surface.</summary>
    public static bool IsRestAccessLogLine(string line) =>
        line.Contains("REST accessed endpoint", StringComparison.Ordinal);

    /// <summary>True for an in-game chat line. Vanilla tags these "[CHAT]", but PalDefender replaces the line
    /// entirely with its own "[Chat::Global]" form, so an exact "[CHAT]" match left PalDefender users with an
    /// empty Chat tab and all their chat in the Server Log (issue #8).</summary>
    public static bool IsChatLine(string line) => line.Contains("[Chat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Shortest password worth redacting. Below this it occurs in ordinary log text.
    /// <see cref="GenerateAdminPassword"/> makes 20.</summary>
    public const int MinRedactableSecret = 8;

    /// <summary>Blank the admin password anywhere it appears in a captured line. Admins type it into chat
    /// (Palworld's <c>/AdminPassword</c>, PalDefender's <c>/adminlogin</c>) and the server echoes it back. Keyed
    /// on the VALUE, not a command name, so it needs no list of mods. Ordinal, since the ini holds it exactly.</summary>
    public static string Redact(string line, string? secret) =>
        string.IsNullOrEmpty(secret) || secret.Length < MinRedactableSecret
            ? line
            : line.Replace(secret, "***", StringComparison.Ordinal);

    /// <summary>Route a captured server-output line: drop noise via <see cref="ShouldLogServerLine"/>, redact the
    /// admin password, chat to the Chat log, the rest to the Server Log. Redacting here covers the log file, the
    /// tabs, and the --console echo at once, since <see cref="Logger"/> builds all three from one string.</summary>
    private void LogServerOutput(string? data)
    {
        if (!ShouldLogServerLine(data))
            return;
        var line = Redact(data!, _captureSecret);
        // Routed on the raw line: a password containing "[Chat" would otherwise take the marker with it.
        if (IsChatLine(data!))
            _logger.Chat(line);
        else
            _logger.Server(line);
    }

    /// <summary>
    /// Graceful stops are serialized through <see cref="StopGate"/>. A force stop bypasses it: killing the process
    /// is what unblocks a graceful ladder that is dragging, so queueing it behind one would make Force Stop wait
    /// out the countdown it was pressed to escape.
    /// </summary>
    private Task StopCoreAsync(bool graceful, int shutdownWaitSeconds, bool restarting, CancellationToken ct,
        Action<bool>? onShutdownRequested = null)
    {
        if (!graceful)
            return StopLadderAsync(graceful, shutdownWaitSeconds, restarting, ct, onShutdownRequested);

        return _stopGate.RunOrJoin(() =>
            StopLadderAsync(graceful, shutdownWaitSeconds, restarting, ct, onShutdownRequested));
    }

    /// <summary>The shutdown ladder. <paramref name="shutdownWaitSeconds"/> is the in-game /shutdown countdown
    /// (0 for restarts and plain Stop, restarts already warned via broadcasts, and a plain Stop is immediate).
    /// <paramref name="restarting"/> picks the state shown while stopping: <see cref="ServerState.Restarting"/>
    /// when a relaunch will follow (restart / recovery), else <see cref="ServerState.Stopping"/>.</summary>
    private async Task StopLadderAsync(bool graceful, int shutdownWaitSeconds, bool restarting, CancellationToken ct,
        Action<bool>? onShutdownRequested = null)
    {
        Process? process;
        PalworldRestClient? rest;
        HealthMonitor? health;
        UpdateMonitor? updateMonitor;
        lock (_gate)
        {
            _manualStop = true;
            process = _process;
            rest = RestClient;
            // Detach the monitors under the lock (consistent with BindProcess), dispose the locals below.
            health = _health;
            _health = null;
            updateMonitor = _updateMonitor;
            _updateMonitor = null;
        }

        // Stop probing so a deliberate shutdown isn't mistaken for a zombie or a new build.
        health?.Dispose();
        updateMonitor?.Dispose();

        if (process is null || process.HasExited)
        {
            State = ServerState.Stopped;
            return;
        }

        State = restarting ? ServerState.Restarting : ServerState.Stopping;

        if (graceful && rest is not null)
        {
            var wait = ShutdownWaitSeconds(shutdownWaitSeconds);
            _logger.Info($"Saving and shutting down (wait {wait}s)...");
            // The shutdown backup does its own fresh /save; otherwise just save.
            if (_config.BackupOnShutdown)
                await _backup.BackupNowAsync(BackupReason.Shutdown, rest, serverRunning: true, ct).ConfigureAwait(false);
            else
                await rest.SaveAsync(ct).ConfigureAwait(false);

            var shutdownAccepted = await rest.ShutdownAsync(wait, "Server is shutting down.").ConfigureAwait(false);
            onShutdownRequested?.Invoke(shutdownAccepted);
            if (!shutdownAccepted)
                _logger.Info("REST /shutdown was rejected, will force-stop if the server doesn't exit.");

            // A real timed shutdown (not a restart, and an actual countdown past the 1s minimum) drives a
            // launcher-side mirror countdown + a "Shutdown Now" affordance. Signal AFTER /shutdown is sent so an
            // accelerate (a second /shutdown(1), see ShutdownNowAsync) is always the later, overriding call; clear
            // it in the finally whichever way the wait ends.
            var timedMirror = !restarting && wait > 1;
            if (timedMirror)
            {
                lock (_gate) _timedShutdownActive = true;
                TimedShutdownChanged?.Invoke(wait);
            }
            try
            {
                if (await WaitForExitAsync(process, TimeSpan.FromSeconds(wait + 30), ct).ConfigureAwait(false))
                    return;

                _logger.Info("Graceful shutdown timed out, forcing stop.");
                await rest.StopAsync(ct).ConfigureAwait(false);
                if (await WaitForExitAsync(process, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false))
                    return;
            }
            finally
            {
                if (timedMirror)
                {
                    lock (_gate) _timedShutdownActive = false;
                    TimedShutdownChanged?.Invoke(null);
                }
            }
        }
        else if (graceful)
        {
            // No REST -> no save/graceful-shutdown is possible (Palworld has no safe OS-signal stop).
            _logger.Info("REST API is off, so the server can't be saved or shut down gracefully. Force-stopping it now. Enable REST for clean shutdowns.");
        }

        KillNow(process);
        // Kill() is asynchronous: HasExited is not reliably true the instant it returns. Recovery and manual
        // restart relaunch right after this, and the launch guard treats a process still reporting
        // HasExited == false as "already running" and skips the relaunch, leaving the server down. Wait for the
        // exit to actually land so that guard sees the truth. CancellationToken.None: we killed it, so we must
        // observe it die even if the surrounding restart was cancelled.
        await WaitForExitAsync(process, TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Restart the server. A manual restart happens immediately (save + bounce now, like a plain Stop); an
    /// update restart warns players with the staged broadcast countdown first, then restarts. Scheduled
    /// restarts don't come through here, the scheduler drives them so the shutdown lands on the chosen time.
    /// </summary>
    /// <param name="attended">True when a user just asked for this and is waiting on it, which is the only
    /// case a failure is allowed to open a dialog. An unattended restart must never block on one: the server is
    /// already stopped by then, and nobody would be there to dismiss it.</param>
    public Task RestartAsync(RestartReason reason, CancellationToken ct = default, bool attended = false) =>
        reason == RestartReason.Manual
            ? RestartNowAsync(reason, ct)                          // manual = immediate, like a plain Stop
            : RestartAsync(reason, DateTime.Now + MaxLead(), ct, attended);  // update = staged broadcast countdown

    /// <summary>
    /// The one restart path shared by update / scheduled / manual restarts: warn players with staged
    /// broadcasts, wait until <paramref name="restartAt"/>, then graceful stop -> start (Start applies
    /// any pending update). Re-entrant restarts are ignored; a user Stop during the countdown aborts it.
    /// </summary>
    public async Task RestartAsync(RestartReason reason, DateTime restartAt, CancellationToken ct = default, bool attended = false)
    {
        CancellationTokenSource restartCts;
        lock (_gate)
        {
            if (_restartInProgress)
            {
                _logger.Debug($"{reason} restart ignored, a restart is already in progress.");
                return;
            }
            _restartInProgress = true;
            _restartCts = restartCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        try
        {
            try
            {
                // Warn players, then wait until restartAt. A user-initiated Stop cancels this.
                await BroadcastAndWaitAsync(reason, restartAt, restartCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.Info($"{reason} restart cancelled before shutdown.");
                return;
            }

            // Past the countdown: stop + (update-)start run to completion regardless of a later cancel.
            // An update-restart forces the SteamCMD update even if "Update on start" is off.
            await StopCoreAsync(graceful: true, shutdownWaitSeconds: 0, restarting: true, ct).ConfigureAwait(false);
            State = ServerState.Restarting; // hold "Restarting" across the update + relaunch (not "Stopped")
            // userInitiated stays false so the relaunch can't clear a deliberate stop. Whether a failure may ask
            // the user is a separate question, which is what attended answers.
            await StartAsync(forceUpdate: reason == RestartReason.Update, userInitiated: false, ct: ct, attended: attended).ConfigureAwait(false);
        }
        finally
        {
            // If the relaunch didn't bring the server up (e.g. install missing / SteamCMD failure),
            // don't leave the UI latched on "Restarting", fall back to Stopped.
            if (!IsRunning())
                State = ServerState.Stopped;
            ClearRestart();
        }
    }

    /// <summary>
    /// Stop + start now, with no broadcast countdown. Used by a manual restart (bounce immediately, like a
    /// plain Stop) and by the scheduler once it has already sent the lead-up announcements and reached the
    /// chosen shutdown time (so the shutdown lands ON that time). Re-entrant-guarded like
    /// <see cref="RestartAsync(RestartReason, DateTime, CancellationToken)"/>.
    /// </summary>
    private async Task RestartNowAsync(RestartReason reason, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_restartInProgress)
            {
                _logger.Debug($"{reason} restart ignored, a restart is already in progress.");
                return;
            }
            _restartInProgress = true;
        }

        try
        {
            await StopCoreAsync(graceful: true, shutdownWaitSeconds: 0, restarting: true, ct).ConfigureAwait(false);
            State = ServerState.Restarting; // hold "Restarting" across the update + relaunch (not "Stopped")
            await StartAsync(userInitiated: false, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            if (!IsRunning())
                State = ServerState.Stopped;
            ClearRestart();
        }
    }

    /// <summary>Send one scheduled-restart lead-up warning, if announcements are on and someone is online.</summary>
    private async Task AnnounceScheduledRestartAsync(int leadMinutes)
    {
        var rest = RestClient;
        if (!_config.RestartBroadcastEnabled || rest is null)
            return;

        var metrics = await rest.GetMetricsAsync().ConfigureAwait(false);
        if (metrics is not { CurrentPlayerNum: > 0 })
            return;

        var message = RestartAnnouncer.Message(RestartReason.Scheduled, TimeSpan.FromMinutes(leadMinutes),
            _config.RestartAnnounceMessage, _config.UpdateAnnounceMessage);
        await rest.AnnounceAsync(message).ConfigureAwait(false);
    }

    private async Task BroadcastAndWaitAsync(RestartReason reason, DateTime restartAt, CancellationToken ct)
    {
        var rest = RestClient;
        var leads = _config.RestartBroadcastLeadMinutes;
        var canBroadcast = _config.RestartBroadcastEnabled && rest is not null && leads.Any(m => m > 0);

        if (canBroadcast)
        {
            var metrics = await rest!.GetMetricsAsync(ct).ConfigureAwait(false);
            if (metrics is { CurrentPlayerNum: > 0 })
            {
                _logger.Info($"{reason} restart: warning {metrics.CurrentPlayerNum} player(s), restarting at {restartAt:HH:mm}.");
                await RestartAnnouncer.RunAsync(leads, restartAt, reason,
                    _config.RestartAnnounceMessage, _config.UpdateAnnounceMessage,
                    (msg, c) => rest!.AnnounceAsync(msg, c), ct).ConfigureAwait(false);
                return;
            }
        }

        _logger.Info($"{reason} restart: no countdown (server empty or REST off), restarting now.");
    }

    /// <summary>Largest configured broadcast lead (0 when broadcasts are off / no valid leads).</summary>
    private TimeSpan MaxLead() =>
        _config.RestartBroadcastEnabled
            ? TimeSpan.FromMinutes(_config.RestartBroadcastLeadMinutes.Where(m => m > 0).DefaultIfEmpty(0).Max())
            : TimeSpan.Zero;

    private void ClearRestart()
    {
        lock (_gate)
        {
            _restartInProgress = false;
            _restartCts?.Dispose();
            _restartCts = null;
        }
    }

    /// <summary><paramref name="adopted"/> = this server was already running when the launcher attached, so
    /// its uptime predates us and it has self-evidently finished booting.</summary>
    private void BindProcess(Process process, bool adopted)
    {
        _process = process;
        _serverStartedUtc = ProcessStartUtc(process);
        // Deliberately NOT the process start time: crash folders are only this run's if they postdate the
        // moment we attached. An adopted server can carry days of older UECC folders from ensures and
        // earlier crashes, and dating the scan from its boot would let one of those be blamed on this exit.
        _crashScanFromUtc = DateTime.UtcNow;
        _sawRunning = adopted;
        process.Exited += OnProcessExited;
        // .NET refuses ExitCode for a process it didn't start ("Process was not started by this object")
        // unless a handle was retained while it was alive, and the scanner hands us a bare one. Setting this
        // retains that handle, so an adopted server's crash still reports its exit code.
        if (adopted)
            TryEnableRaisingEvents(process);
        ApplyProcessTuning(process);

        // Health monitor promotes Starting -> Healthy, feeds the status tiles, and flags zombies.
        _health?.Dispose();
        _health = new HealthMonitor(process, () => RestClient, _config, _logger);
        _health.StateChanged += s => State = s;
        // Hand ReapplyAffinity this monitor's own process, not the _process field. It's the exact process this
        // monitor samples and is non-null for the monitor's whole life, so the re-pin needs no lock, no shared
        // field read, and no null check (an earlier version reached into _process under the gate for no reason).
        _health.Sampled += s => { CacheVersion(s.Version); HealthUpdated?.Invoke(s); ReapplyAffinity(process); };
        _health.ZombieDetected += HandleZombie;
        _health.PlayerChanged += NotifyDiscordOnPlayerChange;
        _health.Start();

        // Update monitor polls SteamCMD's build id while the server runs and triggers an update restart on a
        // new build. Disposed on stop/exit, so it never touches SteamCMD while stopped. Skipped entirely while
        // pinned or with automatic updates off, so a held build is never polled or nudged.
        _updateMonitor?.Dispose();
        _updateMonitor = null;
        if (UpdatePolicy.ShouldRunUpdateMonitor(_config.VersionPinEnabled, _config.AutoUpdateEnabled))
        {
            _updateMonitor = new UpdateMonitor(_config, QueryLatestBuildIdGatedAsync, _steamCmd.ReadInstalledBuildId, BuildDisplay, _logger);
            _updateMonitor.UpdateFound += HandleUpdateFound;
            _updateMonitor.StatusChanged += s => UpdateStatusChanged?.Invoke(s);
            _updateMonitor.Start();
        }
        else
        {
            // No monitor: reflect why in the update-status tile so it isn't stale.
            var installed = _steamCmd.ReadInstalledBuildId() ?? "?";
            UpdateStatusChanged?.Invoke(_config.VersionPinEnabled
                ? string.Format(Strings.Update_Pinned, BuildDisplay(_config.PinnedBuildId.Length > 0 ? _config.PinnedBuildId : installed))
                : string.Format(Strings.Update_AutoUpdateOff, BuildDisplay(installed)));
        }
    }

    /// <summary>
    /// Apply the configured Windows priority + CPU affinity to the server process (best-effort, on every
    /// launch/adopt). Failures (process already gone, access denied) are logged, not fatal. RealTime
    /// isn't offered (needs elevation and can starve the OS). A mask bit for a non-existent core is ignored.
    /// </summary>
    private void ApplyProcessTuning(Process process)
    {
        try
        {
            process.PriorityClass = MapPriority(_config.ServerPriority);

            var cores = Math.Min(Environment.ProcessorCount, 64);
            var systemMask = cores >= 64 ? -1L : (1L << cores) - 1;
            var mask = _config.ServerAffinityMask & systemMask;
            if (mask != 0)
                process.ProcessorAffinity = (IntPtr)mask;

            _logger.Debug($"Process tuning: priority {process.PriorityClass}, affinity {(mask != 0 ? $"0x{mask:X}" : "all cores")}.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            _logger.Info($"Couldn't apply process priority / CPU affinity: {ex.Message}");
        }
    }

    private static ProcessPriorityClass MapPriority(string priority) => priority switch
    {
        "BelowNormal" => ProcessPriorityClass.BelowNormal,
        "AboveNormal" => ProcessPriorityClass.AboveNormal,
        "High" => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };

    /// <summary>
    /// Re-pin the configured CPU affinity if it has drifted (no-op when unrestricted). Unreal resets the
    /// process affinity to all cores during startup, clobbering the initial set in <see cref="BindProcess"/>,
    /// so the health probe calls this each tick: it reads the current affinity and re-applies the mask only
    /// when it doesn't match. Priority isn't reset by the engine, so it isn't re-applied here. Operates on the
    /// process the health monitor is sampling (passed in), so there's no shared field to read or race.
    /// </summary>
    private void ReapplyAffinity(Process process)
    {
        if (_config.ServerAffinityMask == 0)
            return; // no restriction configured
        try
        {
            // The external server can exit at any instant; poking a dead process throws, which is caught below.
            if (process.HasExited)
                return;
            var cores = Math.Min(Environment.ProcessorCount, 64);
            var systemMask = cores >= 64 ? -1L : (1L << cores) - 1;
            var mask = _config.ServerAffinityMask & systemMask;
            if (mask != 0 && process.ProcessorAffinity.ToInt64() != mask)
            {
                process.ProcessorAffinity = (IntPtr)mask;
                _logger.Debug($"Re-pinned CPU affinity to 0x{mask:X} (something reset it).");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Best-effort; not fatal.
        }
    }

    /// <summary>Read the latest published build id under the SteamCMD gate (so it can't overlap another run).</summary>
    private async Task<string?> QueryLatestBuildIdGatedAsync(CancellationToken ct)
    {
        var steamLog = new Progress<string>(_logger.SteamCmd);
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Self-heal a missing SteamCMD before the build-id query, hidden so a background check stays silent.
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct, visible: false).ConfigureAwait(false);
            return await _steamCmd.QueryLatestBuildIdAsync(null, ct).ConfigureAwait(false);
        }
        finally
        {
            _steamGate.Release();
        }
    }

    private void HandleUpdateFound(string buildId)
    {
        _targetBuildId = buildId;
        if (_config.DiscordNotifyLifecycle)
            _discord.Notify("⬆️ A new Palworld server build was found, updating and restarting.");
        FireAndForget(() => RestartAsync(RestartReason.Update), "Update restart");
    }

    /// <summary>Discord lifecycle notifications on meaningful state edges (up / down / crash-backoff).</summary>
    /// <summary>Post a player join/leave to the Discord webhook (when enabled with a URL + player notifications on).</summary>
    private void NotifyDiscordOnPlayerChange(HealthMonitor.RosterChange change, int online)
    {
        if (!_config.DiscordNotifyPlayers)
            return;
        var name = SanitizeName(change.Name);
        _discord.Notify(change.Joined
            ? $"➡️ **{name}** joined ({online} online)"
            : $"⬅️ **{name}** left ({online} online)");
    }

    private void NotifyDiscordOnStateChange(ServerState state)
    {
        if (_config.DiscordNotifyLifecycle)
        {
            var message = state switch
            {
                ServerState.Healthy when _lastNotifiedState is ServerState.Starting or ServerState.Restarting or ServerState.RestUnreachable => "🟢 Palworld server is up.",
                ServerState.Stopped => "🔴 Palworld server stopped.",
                ServerState.Backoff => "⚠️ Palworld server crashed repeatedly, auto-restart suspended.",
                _ => null,
            };
            if (message is not null)
                _discord.Notify(message);
        }
        _lastNotifiedState = state;
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        bool wasManual;
        bool reachedRunning;
        DateTime? startedUtc;
        DateTime? crashScanFromUtc;
        HealthMonitor? health;
        UpdateMonitor? updateMonitor;
        lock (_gate)
        {
            wasManual = _manualStop;
            reachedRunning = _sawRunning;
            startedUtc = _serverStartedUtc;
            crashScanFromUtc = _crashScanFromUtc;
            _process = null;
            _serverStartedUtc = null;
            _crashScanFromUtc = null;
            health = _health;
            _health = null;
            updateMonitor = _updateMonitor;
            _updateMonitor = null;
        }

        // Read off the sender: _process is already null above, and the exit code is the only thing that
        // separates a fatal assert (3) from an external kill (1) or a clean stop (0).
        var exitCode = TryReadExitCode(sender as Process);
        var uptime = startedUtc is null ? TimeSpan.Zero : DateTime.UtcNow - startedUtc.Value;

        health?.Dispose();
        updateMonitor?.Dispose();

        State = ServerState.Stopped;

        if (_disposed || wasManual)
        {
            _logger.Info("Server stopped.");
            return;
        }

        var summary = CrashReport.DescribeExit(exitCode, uptime, reachedRunning);

        if (!_config.RestartOnCrash)
        {
            _logger.Info($"{summary} Auto-restart disabled.");
            FireAndForget(() => LogCrashReasonAsync(crashScanFromUtc), "Crash reason");
            return;
        }

        if (AllowRestart())
        {
            // Fast relaunch to restore service, no update check on a crash. The reason is read first so it
            // lands under the crash line rather than below the next launch banner. Bounded by the reader's
            // own retries, so this delays the relaunch by at most a couple hundred milliseconds.
            _logger.Info($"{summary} Restarting.");
            FireAndForget(async () =>
            {
                // finally, not a bare sequence: recovery must not hinge on a diagnostic. Anything the reason
                // read doesn't catch would otherwise swallow the relaunch and leave the server down.
                try { await LogCrashReasonAsync(crashScanFromUtc).ConfigureAwait(false); }
                finally { await LaunchServerAsync().ConfigureAwait(false); }
            }, "Crash relaunch");
        }
        else
        {
            State = ServerState.Backoff;
            _logger.Error($"{summary} Auto-restart suspended after repeated crashes. Fix the issue, then Start manually.");
            FireAndForget(() => LogCrashReasonAsync(crashScanFromUtc), "Crash reason");
        }
    }

    /// <summary>Best-effort: a server started by another user, or one that died between the scan and here,
    /// simply keeps the old behavior of reporting no exit code.</summary>
    private void TryEnableRaisingEvents(Process process)
    {
        try { process.EnableRaisingEvents = true; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Debug($"Couldn't watch the adopted server's exit directly: {ex.Message}");
        }
    }

    /// <summary>When the server process really started. Adopting a long-running server would otherwise date
    /// its uptime from adoption, which both mislabels a crash as a startup failure and hands the restart
    /// scheduler a uptime far below the truth. Denied or already-gone processes fall back to now.</summary>
    private static DateTime ProcessStartUtc(Process process)
    {
        try { return process.StartTime.ToUniversalTime(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return DateTime.UtcNow;
        }
    }

    private static int? TryReadExitCode(Process? process)
    {
        try { return process?.ExitCode; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Log why Unreal says the server died. Off the exit path (it touches the disk) so a crash
    /// relaunch isn't held up waiting on it.</summary>
    private async Task LogCrashReasonAsync(DateTime? crashScanFromUtc)
    {
        if (crashScanFromUtc is null)
            return;

        var crash = await CrashReport.ReadAsync(_config.ServerRoot, crashScanFromUtc.Value).ConfigureAwait(false);
        if (crash is null)
        {
            _logger.Error("No crash report found. Server may have been force-stopped.");
            return;
        }

        _logger.Error(crash.Value.Reason is null
            ? "A crash report was written but its reason couldn't be read, it may be incomplete."
            : $"Crash reason: {crash.Value.Reason}");
        _logger.Error($"More information for this crash can be found at {crash.Value.Directory}.");
    }

    private void HandleZombie()
    {
        if (!_config.RestartOnCrash)
        {
            _logger.Info("Server is unresponsive but auto-restart is disabled.");
            return;
        }
        FireAndForget(RecoverAsync, "Zombie recovery");
    }

    private async Task RecoverAsync()
    {
        if (!AllowRestart())
        {
            State = ServerState.Backoff;
            _logger.Error("Auto-recovery suspended after the server kept going unresponsive. Fix the issue, then Start manually.");
            return;
        }
        _logger.Info("Server is unresponsive, stopping and relaunching it...");
        await StopCoreAsync(graceful: true, shutdownWaitSeconds: 0, restarting: true, CancellationToken.None).ConfigureAwait(false);
        await LaunchServerAsync().ConfigureAwait(false);
    }

    /// <summary>Circuit breaker, see <see cref="RestartBudget"/>. Locked because crash-relaunch
    /// (Process.Exited) and zombie recovery can both call it off-thread.</summary>
    private bool AllowRestart()
    {
        lock (_gate)
            return _restartBudget.TryConsume(DateTime.UtcNow);
    }

    /// <summary>Run a fire-and-forget lifecycle task, logging any exception instead of losing it to GC.</summary>
    private void FireAndForget(Func<Task> operation, string description) => _ = RunLoggedAsync(operation, description);

    private async Task RunLoggedAsync(Func<Task> operation, string description)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"{description} failed", ex);
        }
    }

    private void RebuildRestClient()
    {
        RestClient?.Dispose();
        var settings = IniReader.ReadFile(PalWorldSettingsPath);
        RestClient = settings.RestApiUsable
            ? new PalworldRestClient(settings.RestApiPortOrDefault, settings.AdminPassword!)
            : null;
    }

    private bool IsRunning()
    {
        lock (_gate)
            return IsRunningNoLock();
    }

    /// <summary>Running check for callers that already hold <c>_gate</c> (used for the atomic launch check).</summary>
    private bool IsRunningNoLock() => _process is { HasExited: false };

    /// <summary>Wait up to <paramref name="timeout"/> for a process to exit, reporting whether it actually did.
    /// Internal rather than private so the headless CLI stop path gets the same semantics.</summary>
    internal static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
    }

    private void KillNow(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _logger.Info("Server process force-stopped.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Info($"Force stop failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the server command line from config. The stdout-capture args (`-stdout -FullStdOutLogOutput
    /// -UTF8Output`) are always included, that's how the Server tab is fed, since Palworld writes no log
    /// file. Optional args are omitted when at their "unset" value so they
    /// don't override the ini (e.g. MaxPlayers=0 defers to ServerPlayerMaxNum). Pure/static = testable.
    /// </summary>
    public static IReadOnlyList<string> BuildLaunchArgs(LauncherConfig config, int queryPort)
    {
        var args = new List<string>();

        if (config.PerformanceThreads)
        {
            args.AddRange(["-useperfthreads", "-NoAsyncLoadingThread", "-UseMultithreadForDS"]);
            if (config.WorkerThreads > 0)
                args.Add($"-NumberOfWorkerThreadsServer={config.WorkerThreads}");
        }

        // -stdout and -FullStdOutLogOutput only produce output as a pair. Either alone yields a single line, and
        // dropping -FullStdOutLogOutput loses "Game version" and "REST API started" along with the engine spam,
        // so neither is noise to trim. -log used to be passed too and was dropped after measuring a vanilla
        // server with a real player, where chat and the "[LOG] joined the server" lines both arrived without
        // it. That measurement says nothing about UE4SS plugins such as PalDefender, which write chat through
        // their own sink. Testing any of this needs a fresh launch, since an adopted server has no stdout.
        args.AddRange(["-stdout", "-FullStdOutLogOutput", "-UTF8Output"]);
        args.Add($"-port={config.ServerPort}");
        args.Add($"-QueryPort={queryPort}");

        if (config.MaxPlayers > 0)
            args.Add($"-players={config.MaxPlayers}");
        if (config.CommunityServer)
            args.Add("-publiclobby");
        if (!string.IsNullOrWhiteSpace(config.PublicIp))
            args.Add($"-publicip={config.PublicIp.Trim()}");
        if (config.PublicPortArg > 0)
            args.Add($"-publicport={config.PublicPortArg}");
        if (!string.IsNullOrWhiteSpace(config.LogFormat))
            args.Add($"-logformat={config.LogFormat.Trim()}");

        // Split on any whitespace (space/tab/newline) so the multi-line "Advanced" box works too.
        args.AddRange(config.ExtraServerArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return args;
    }

    /// <summary>First UDP port free for binding at or above <paramref name="start"/> (mirrors the old tool's query-port probe).</summary>
    public static int FindFreeUdpPort(int start)
    {
        for (var port = start; port <= 65535; port++)
        {
            try
            {
                using var probe = new UdpClient(port);
                return port;
            }
            catch (SocketException)
            {
                // In use, try next.
            }
        }
        return start;
    }

    public void Dispose()
    {
        _disposed = true;
        _scheduler.Dispose();
        _backupScheduler.Dispose();
        _discord.Dispose();
        _discordBot.Dispose();
        _ipc.Dispose();
        _health?.Dispose();
        _updateMonitor?.Dispose();
        RestClient?.Dispose();
        _restartCts?.Dispose();
        _steamGate.Dispose();
        if (_process is not null)
            _process.Exited -= OnProcessExited;
    }
}

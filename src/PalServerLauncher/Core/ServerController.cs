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
    private HealthMonitor? _health;
    private UpdateMonitor? _updateMonitor;
    private readonly RestartScheduler _scheduler;
    private readonly BackupService _backup;
    private readonly BackupScheduler _backupScheduler;
    private readonly DiscordNotifier _discord;
    private readonly DiscordBotService _discordBot;
    private ServerState _lastNotifiedState = ServerState.Stopped;
    private readonly SemaphoreSlim _steamGate = new(1, 1); // serialize all SteamCMD runs - never two at once

    /// <summary>Read/write access to PalWorldSettings.ini game settings (used by the settings editor; gated to stopped).</summary>
    public GameSettingsService GameSettings { get; }

    /// <summary>Steam Workshop mod management (the Mods dialog scans / opens the folder through it; the launch
    /// path downloads + applies mods through it).</summary>
    public ModService ModService { get; }
    private readonly List<DateTime> _restartTimes = new();
    private DateTime? _serverStartedUtc;
    private bool _manualStop;
    private readonly RelaunchGate _relaunchGate = new(); // suppresses auto-recovery / restart relaunch after a deliberate stop, until the next user Start
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

        return $"### 🖥️ Server status\n"
             + $"**State:** {state}\n"
             + $"**Version:** {info?.Version ?? "?"}\n"
             + $"**Players:** {metrics.CurrentPlayerNum} / {metrics.MaxPlayerNum}\n"
             + $"**FPS:** {metrics.ServerFps}\n"
             + $"**Frame time:** {metrics.ServerFrameTime:0.##} ms\n"
             + $"**Uptime:** {uptimeText}\n"
             + $"**In-game days:** {metrics.Days}\n"
             + $"**Base camps:** {metrics.BaseCampNum}";
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
        FireAndForget(() => StartAsync(), "Discord start");
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
            }
            _logger.Debug($"State: {previous} -> {value}");
            StateChanged?.Invoke(value);
        }
    }

    /// <summary>The managed process, or null when stopped. Exposed so the health monitor can read memory / I/O counters.</summary>
    public Process? Process => _process;

    /// <summary>REST client built from the current PalWorldSettings.ini, or null if the API isn't usable.</summary>
    public PalworldRestClient? RestClient { get; private set; }

    private string PalWorldSettingsPath => Path.Combine(
        _config.ServerRoot, LauncherConfig.ServerFolderName, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");

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
            BindProcess(existing);
            RebuildRestClient();
        }
        _logger.Info(all.Count == 1
            ? $"Startup scan: detected 1 running server ({pidList}), adopted so it can be controlled."
            : $"Startup scan: detected {all.Count} running server instances ({pidList}), adopted PID {existing.Id}; use Shut Down All to stop them.");
        State = ServerState.Starting; // health monitor promotes to Healthy once REST responds
        return true;
    }

    /// <summary>True when the server binary exists on disk.</summary>
    public bool IsInstalled => File.Exists(ProcessScanner.ExpectedExePath(_config.ServerRoot));

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

    /// <summary>
    /// Install (first run) or update + validate the server via SteamCMD. This is an explicit,
    /// user-triggered action, it never runs implicitly from <see cref="StartAsync"/>, so a plain
    /// "Start" can never surprise the user with a multi-GB download. Refuses while running (locked files).
    /// </summary>
    public async Task InstallOrUpdateAsync(bool validate = true, CancellationToken ct = default)
    {
        if (IsRunning())
        {
            _logger.Info("Stop the server before installing or updating.");
            return;
        }

        _logger.Info(_config.HideSteamCmdWindow
            ? "Installing / updating server via SteamCMD (live log in the SteamCMD tab)..."
            : "Installing / updating server via SteamCMD (a console window will open; live log in the SteamCMD tab)...");

        var steamLog = new Progress<string>(line => _logger.SteamCmd(line));
        // Mirror SteamCMD's own console log into the SteamCMD tab while it runs.
        using var tail = new FileTailer(_steamCmd.ConsoleLogPath, _logger.SteamCmd, fromStart: false);

        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct).ConfigureAwait(false);
            var exit = await _steamCmd.InstallOrUpdateServerAsync(
                validate: validate, visible: !_config.HideSteamCmdWindow, steamLog, ct).ConfigureAwait(false);

            _logger.Info(exit == 0
                ? $"Install/update complete (build {_steamCmd.ReadInstalledBuildId() ?? "?"})."
                : $"SteamCMD exited with code {exit}. Check the SteamCMD tab.");
        }
        finally
        {
            _steamGate.Release();
        }
    }

    /// <summary>Outcome of a read-only manual update check (the "Check for Update" button).</summary>
    public enum UpdateCheckResult { UpToDate, UpdateAvailable, CheckFailed }

    /// <summary>
    /// Read-only manual update check: compares the installed build id to the latest published one via
    /// SteamCMD WITHOUT downloading anything. The caller decides whether to then run the update. Reuses
    /// the same gated build-id query + comparison the background <see cref="UpdateMonitor"/> uses.
    /// </summary>
    public async Task<(UpdateCheckResult Result, string? LatestBuildId)> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var installed = _steamCmd.ReadInstalledBuildId();
        UpdateStatusChanged?.Invoke("Checking for updates...");
        var latest = await QueryLatestBuildIdGatedAsync(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(latest))
        {
            UpdateStatusChanged?.Invoke($"Update check failed (build {installed ?? "?"})");
            return (UpdateCheckResult.CheckFailed, null);
        }
        if (UpdateMonitor.IsUpdateAvailable(installed, latest))
        {
            UpdateStatusChanged?.Invoke($"New build {latest} available");
            return (UpdateCheckResult.UpdateAvailable, latest);
        }
        UpdateStatusChanged?.Invoke($"Up to date (build {installed ?? "?"})");
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
    public async Task StartAsync(bool forceUpdate = false, bool userInitiated = true, CancellationToken ct = default)
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

        // A user Start clears a prior deliberate-stop suppression. A restart passes false so a Stop or Force
        // Shutdown during the restart keeps the server down instead of being undone by the restart's relaunch.
        lock (_gate)
            _relaunchGate.OnStart(userInitiated);

        // Back up before the update: SteamCMD can wipe PalWorldSettings.ini, and the server is stopped
        // here so this snapshots the on-disk (last-autosave) state.
        if (_config.BackupOnStartup)
            await _backup.BackupNowAsync(BackupReason.Startup, rest: null, serverRunning: false, ct).ConfigureAwait(false);

        // The per-start update is optional; an explicit update-restart forces it regardless of the toggle.
        if (forceUpdate || _config.UpdateOnStart)
            await UpdateInPlaceAsync(ct).ConfigureAwait(false);
        else
            _logger.Info("Skipping the start-time update check (Update on start is off).");

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
    public Task ShutdownWithCountdownAsync(int seconds, CancellationToken ct = default)
    {
        lock (_gate)
        {
            _restartCts?.Cancel();
            _relaunchGate.SuppressForDeliberateStop(); // a deliberate shutdown stays stopped, like a plain Stop
        }
        return StopCoreAsync(graceful: true, shutdownWaitSeconds: seconds, restarting: false, ct);
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

        _logger.Info("Force shutdown requested, killing the server process now.");
        KillNow(process);
    }

    /// <summary>Recompute the next-restart/next-backup UI text immediately (after a schedule setting change).</summary>
    public void RefreshScheduleText()
    {
        _scheduler.Refresh();
        _backupScheduler.Refresh();
    }

    /// <summary>Run SteamCMD app_update in place before launch (the "always current on boot" step).</summary>
    private async Task UpdateInPlaceAsync(CancellationToken ct)
    {
        _logger.Info("Checking for a server update before launch...");
        var steamLog = new Progress<string>(_logger.SteamCmd);
        using var tail = new FileTailer(_steamCmd.ConsoleLogPath, _logger.SteamCmd, fromStart: false);

        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exit = await _steamCmd.InstallOrUpdateServerAsync(
                validate: _config.VerifyOnUpdate, visible: !_config.HideSteamCmdWindow, steamLog, ct).ConfigureAwait(false);
            var buildId = _steamCmd.ReadInstalledBuildId() ?? "?";
            if (exit == 0)
            {
                _logger.Info($"Server up to date (build {buildId}).");
                UpdateStatusChanged?.Invoke($"Up to date (build {buildId})");
            }
            else
            {
                _logger.Info($"SteamCMD update exited with code {exit}, launching the installed build anyway.");
            }
        }
        finally
        {
            _steamGate.Release();
        }
    }

    /// <summary>
    /// Bring the server's mods in line with config just before launch (a restart routes through StartAsync, so
    /// it re-syncs). Mods on: download each enabled Workshop id with the connected Steam account (incremental,
    /// so it doubles as an up-to-date check), copy it into the server's Mods\Workshop, resolve its PackageName,
    /// then write PalModSettings.ini enabling every enabled mod (downloaded + dropped-in). Mods off: turn the
    /// ini's master flag off if a previous run left it on. A failed sync never blocks launch, it logs and
    /// continues, same posture as the update step. SteamCMD work runs under <see cref="_steamGate"/>.
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

            // Enable every enabled mod that resolves to a PackageName (from the cached value or its Info.json).
            var active = new List<string>();
            foreach (var mod in enabled)
            {
                var pkg = !string.IsNullOrWhiteSpace(mod.PackageName)
                    ? mod.PackageName
                    : string.IsNullOrWhiteSpace(mod.WorkshopId) ? null : ModService.ResolvePackageName(mod.WorkshopId);
                if (!string.IsNullOrWhiteSpace(pkg))
                    active.Add(pkg);
                else
                    _logger.Info($"Mod '{ModDisplayName(mod)}' has no PackageName yet, it won't activate until it's downloaded or scanned.");
            }
            ModService.ApplyPalModSettings(globalEnable: true, active);
        }
        catch (Exception ex)
        {
            _logger.Error("Mod sync failed, launching without applying mod changes", ex);
        }
    }

    /// <summary>Download (incrementally) each enabled Workshop mod under the SteamCMD gate, copy it into the
    /// server's Mods\Workshop, and cache its resolved PackageName in the in-memory config. Stops early on an
    /// auth failure so the user is told to reconnect once, not once per mod.</summary>
    private async Task DownloadModsAsync(IReadOnlyList<ModEntry> mods, CancellationToken ct)
    {
        var steamLog = new Progress<string>(_logger.SteamCmd);
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct).ConfigureAwait(false);
            foreach (var mod in mods)
            {
                var result = await _steamCmd.DownloadWorkshopItemAsync(_config.SteamUsername, mod.WorkshopId, steamLog, ct).ConfigureAwait(false);
                if (result == SteamCmd.WorkshopDownloadResult.AuthFailed)
                {
                    _logger.Error("Steam sign-in expired, reconnect your account in the Mods dialog. Skipping the remaining downloads.");
                    return;
                }
                if (result != SteamCmd.WorkshopDownloadResult.Ok)
                    continue; // a single failed download shouldn't stop the others

                ModService.CopyDownloadedMod(mod.WorkshopId, _steamCmd.WorkshopContentDir(mod.WorkshopId));
                var pkg = ModService.ResolvePackageName(mod.WorkshopId);
                if (!string.IsNullOrWhiteSpace(pkg))
                    mod.PackageName = pkg; // same object the VM holds; the dialog's Save persists it
            }
        }
        finally
        {
            _steamGate.Release();
        }
    }

    /// <summary>A friendly identifier for a mod in a log line: its name, else its Workshop id, else "(local mod)".</summary>
    private static string ModDisplayName(ModEntry mod) =>
        !string.IsNullOrWhiteSpace(mod.ModName) ? mod.ModName
        : !string.IsNullOrWhiteSpace(mod.WorkshopId) ? mod.WorkshopId
        : "(local mod)";

    /// <summary>
    /// Run SteamCMD's interactive one-time sign-in so it caches a session for Workshop downloads. Its own visible
    /// window prompts for the password + Steam Guard, the launcher only passes the username. Under the gate.
    /// </summary>
    public async Task<bool> ConnectSteamAsync(string username, CancellationToken ct = default)
    {
        var steamLog = new Progress<string>(_logger.SteamCmd);
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _steamCmd.EnsureSteamCmdAsync(steamLog, ct).ConfigureAwait(false);
            await _steamCmd.ConnectAccountAsync(username, steamLog, ct).ConfigureAwait(false);
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

    /// <summary>Launch the installed server (no update). Used by Start (after updating), crash-restart, and recovery.</summary>
    private Task LaunchServerAsync(CancellationToken ct = default)
    {
        if (IsRunning())
            return Task.CompletedTask;

        // The REST API being disabled is not fatal, we still launch (a fresh install has no
        // PalWorldSettings.ini until the server generates one on first boot). We just lose
        // stats/health/graceful-shutdown until the user enables it.
        var settings = IniReader.ReadFile(PalWorldSettingsPath);
        if (!settings.RestApiUsable)
        {
            _logger.Info(File.Exists(PalWorldSettingsPath)
                ? "REST API not enabled, starting without stats/health. Set AdminPassword + RESTAPIEnabled=True in PalWorldSettings.ini for full control."
                : "First run, the server will generate its config. Afterward, set AdminPassword + RESTAPIEnabled=True for stats/health, then restart.");
        }

        var exe = ProcessScanner.ExpectedExePath(_config.ServerRoot);
        var queryPort = FindFreeUdpPort(27015);
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
            BindProcess(process);
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

    /// <summary>True for an in-game chat line (the server tags these "[CHAT]").</summary>
    public static bool IsChatLine(string line) => line.Contains("[CHAT]", StringComparison.Ordinal);

    /// <summary>Route a captured server-output line: drop noise via <see cref="ShouldLogServerLine"/>, send chat
    /// to the Chat log, and everything else to the Server Log, so the Server Log stays focused on server events.</summary>
    private void LogServerOutput(string? data)
    {
        if (!ShouldLogServerLine(data))
            return;
        if (IsChatLine(data!))
            _logger.Chat(data!);
        else
            _logger.Server(data!);
    }

    /// <summary>The shutdown ladder. <paramref name="shutdownWaitSeconds"/> is the in-game /shutdown countdown
    /// (0 for restarts and plain Stop, restarts already warned via broadcasts, and a plain Stop is immediate).
    /// <paramref name="restarting"/> picks the state shown while stopping: <see cref="ServerState.Restarting"/>
    /// when a relaunch will follow (restart / recovery), else <see cref="ServerState.Stopping"/>.</summary>
    private async Task StopCoreAsync(bool graceful, int shutdownWaitSeconds, bool restarting, CancellationToken ct)
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
            _logger.Info("REST API off, can't save or graceful-shutdown; force-stopping (autosave limits loss). Enable REST for clean shutdowns.");
        }

        KillNow(process);
    }

    /// <summary>
    /// Restart the server. A manual restart happens immediately (save + bounce now, like a plain Stop); an
    /// update restart warns players with the staged broadcast countdown first, then restarts. Scheduled
    /// restarts don't come through here, the scheduler drives them so the shutdown lands on the chosen time.
    /// </summary>
    public Task RestartAsync(RestartReason reason, CancellationToken ct = default) =>
        reason == RestartReason.Manual
            ? RestartNowAsync(reason, ct)                          // manual = immediate, like a plain Stop
            : RestartAsync(reason, DateTime.Now + MaxLead(), ct);  // update = staged broadcast countdown

    /// <summary>
    /// The one restart path shared by update / scheduled / manual restarts: warn players with staged
    /// broadcasts, wait until <paramref name="restartAt"/>, then graceful stop -> start (Start applies
    /// any pending update). Re-entrant restarts are ignored; a user Stop during the countdown aborts it.
    /// </summary>
    public async Task RestartAsync(RestartReason reason, DateTime restartAt, CancellationToken ct = default)
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
            await StartAsync(forceUpdate: reason == RestartReason.Update, userInitiated: false, ct: ct).ConfigureAwait(false);
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

    private void BindProcess(Process process)
    {
        _process = process;
        _serverStartedUtc = DateTime.UtcNow;
        process.Exited += OnProcessExited;
        ApplyProcessTuning(process);

        // Health monitor promotes Starting -> Healthy, feeds the status tiles, and flags zombies.
        _health?.Dispose();
        _health = new HealthMonitor(process, () => RestClient, _config, _logger);
        _health.StateChanged += s => State = s;
        // Hand ReapplyAffinity this monitor's own process, not the _process field. It's the exact process this
        // monitor samples and is non-null for the monitor's whole life, so the re-pin needs no lock, no shared
        // field read, and no null check (an earlier version reached into _process under the gate for no reason).
        _health.Sampled += s => { HealthUpdated?.Invoke(s); ReapplyAffinity(process); };
        _health.ZombieDetected += HandleZombie;
        _health.PlayerChanged += NotifyDiscordOnPlayerChange;
        _health.Start();

        // Update monitor polls SteamCMD's build id while the server runs; on a new build it triggers
        // an update restart. Disposed on stop/exit, so it never touches SteamCMD while stopped.
        _updateMonitor?.Dispose();
        _updateMonitor = new UpdateMonitor(_config, QueryLatestBuildIdGatedAsync, _steamCmd.ReadInstalledBuildId, _logger);
        _updateMonitor.UpdateFound += HandleUpdateFound;
        _updateMonitor.StatusChanged += s => UpdateStatusChanged?.Invoke(s);
        _updateMonitor.Start();
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
        await _steamGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _steamCmd.QueryLatestBuildIdAsync(null, ct).ConfigureAwait(false);
        }
        finally
        {
            _steamGate.Release();
        }
    }

    private void HandleUpdateFound()
    {
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
                ServerState.Healthy when _lastNotifiedState is ServerState.Starting or ServerState.Restarting => "🟢 Palworld server is up.",
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
        HealthMonitor? health;
        UpdateMonitor? updateMonitor;
        lock (_gate)
        {
            wasManual = _manualStop;
            _process = null;
            _serverStartedUtc = null;
            health = _health;
            _health = null;
            updateMonitor = _updateMonitor;
            _updateMonitor = null;
        }

        health?.Dispose();
        updateMonitor?.Dispose();

        State = ServerState.Stopped;

        if (_disposed || wasManual)
        {
            _logger.Info("Server stopped.");
            return;
        }

        if (!_config.RestartOnCrash)
        {
            _logger.Info("Server exited unexpectedly (crash). Auto-restart disabled.");
            return;
        }

        if (AllowRestart())
        {
            // Fast relaunch to restore service, no update check on a crash.
            _logger.Info("Server exited unexpectedly (crash), restarting.");
            FireAndForget(() => LaunchServerAsync(), "Crash relaunch");
        }
        else
        {
            State = ServerState.Backoff;
            _logger.Error("Server crashed repeatedly, auto-restart suspended (circuit breaker). Fix the issue, then Start manually.");
        }
    }

    private void HandleZombie()
    {
        if (!_config.RestartOnCrash)
        {
            _logger.Info("Zombie detected but auto-restart is disabled.");
            return;
        }
        FireAndForget(RecoverAsync, "Zombie recovery");
    }

    private async Task RecoverAsync()
    {
        if (!AllowRestart())
        {
            State = ServerState.Backoff;
            _logger.Error("Server wedged repeatedly, auto-recovery suspended (circuit breaker). Fix the issue, then Start manually.");
            return;
        }
        _logger.Info("Recovering wedged server (stop + relaunch)...");
        await StopCoreAsync(graceful: true, shutdownWaitSeconds: 0, restarting: true, CancellationToken.None).ConfigureAwait(false);
        await LaunchServerAsync().ConfigureAwait(false);
    }

    /// <summary>Circuit breaker: allow at most 3 auto-restarts within a 5-minute rolling window.
    /// Locked because crash-relaunch (Process.Exited) and zombie recovery can both call it off-thread.</summary>
    private bool AllowRestart()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            _restartTimes.RemoveAll(t => now - t > TimeSpan.FromMinutes(5));
            if (_restartTimes.Count >= 3)
                return false;
            _restartTimes.Add(now);
            return true;
        }
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

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout, CancellationToken ct)
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
                _logger.Info("Server process killed.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.Info($"Kill failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Build the server command line from config. The stdout-capture args (`-log -stdout
    /// -FullStdOutLogOutput -UTF8Output`) are always included (that's how the Server Log tab is fed -
    /// Palworld writes no log file). Optional args are omitted when at their "unset" value so they
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

        args.AddRange(["-log", "-stdout", "-FullStdOutLogOutput", "-UTF8Output"]);
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
        _health?.Dispose();
        _updateMonitor?.Dispose();
        RestClient?.Dispose();
        _restartCts?.Dispose();
        _steamGate.Dispose();
        if (_process is not null)
            _process.Exited -= OnProcessExited;
    }
}

using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.State;

namespace PalServerLauncher.ViewModels;

/// <summary>
/// Backs <c>MainWindow</c>. A single multi-state primary button (Install/Start/Stop/transitional)
/// plus separate Restart and Update/Verify commands drive <see cref="ServerController"/>. All
/// user-visible text comes through the shared <see cref="Logger"/> (which also writes the session
/// file); logger and controller callbacks arrive on background threads and are marshaled to the UI.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly Logger _logger;
    private readonly LauncherConfig _config;
    private readonly ServerController _controller;
    private readonly DispatcherTimer _busyAnimationTimer;
    private int _busyDots;
    // Reveals the Force Shutdown button only after the server has been stuck in a transitional state for this long.
    private readonly DispatcherTimer _forceShutdownRevealTimer;
    // Ticks the display-only mirror countdown for a timed shutdown (1s), while the server runs the real countdown.
    private readonly DispatcherTimer _shutdownCountdownTimer;

    // The up-to-3 announce lead-minute marks as independent slots (blank = off), so a user can announce
    // at just one mark. Populated from config in the constructor; digit-gated in the view.
    private readonly string[] _leadSlots = new string[3];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText), nameof(PrimaryActionKind), nameof(StateText), nameof(UpdateActionsEnabled), nameof(CanCheckForUpdate), nameof(CanCheckPorts), nameof(CanUseServerCommands))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand), nameof(RestartCommand), nameof(ValidateFilesCommand))]
    private ServerState _state = ServerState.Stopped;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText), nameof(PrimaryActionKind), nameof(UpdateActionsEnabled), nameof(CanCheckForUpdate), nameof(CanTogglePin), nameof(CanImport), nameof(ImportVisible))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand), nameof(RestartCommand), nameof(ValidateFilesCommand), nameof(BackupNowCommand))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText), nameof(PrimaryActionKind), nameof(UpdateActionsEnabled), nameof(CanCheckForUpdate), nameof(CanCheckPorts), nameof(CanImport))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand), nameof(RestartCommand), nameof(ValidateFilesCommand), nameof(BackupNowCommand))]
    private bool _isBusy;

    [ObservableProperty] private string _version = "-";
    [ObservableProperty] private string _fps = "-";
    [ObservableProperty] private string _cpu = "-";
    [ObservableProperty] private string _memory = "-";
    [ObservableProperty] private string _players = "-";
    [ObservableProperty] private string _uptime = "-";
    [ObservableProperty] private string _nextRestart = "-";
    [ObservableProperty] private string _nextBackup = "-";
    [ObservableProperty] private string _updateStatus = "-";

    /// <summary>Drives the Force Shutdown button's visibility. It stays hidden until the server has been stuck
    /// in a transitional state (Starting / Stopping / Restarting) past the reveal delay, so it only surfaces
    /// as an escape hatch when a start, stop, or restart is dragging.</summary>
    [ObservableProperty] private bool _isForceShutdownVisible;

    /// <summary>Remaining seconds on a timed shutdown's mirror countdown, or null when none is running. Turns the
    /// primary button into the amber "Shutdown Now" (accelerate) affordance while the server counts down.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryActionText), nameof(PrimaryActionKind))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private int? _shutdownRemainingSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublicIpDisplay), nameof(ConnectionInfo), nameof(CanCopyConnectionInfo))]
    private string _publicIp = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PublicIpDisplay))]
    private bool _isIpRevealed;

    /// <summary>General tab: every line. The Server / Chat / Players / SteamCmd tabs each show only their channel.</summary>
    public ObservableCollection<string> LogGeneral { get; } = new();
    public ObservableCollection<string> LogServer { get; } = new();
    public ObservableCollection<string> LogChat { get; } = new();
    public ObservableCollection<string> LogPlayerJoin { get; } = new();
    public ObservableCollection<string> LogSteamCmd { get; } = new();

    /// <summary>Raised on the UI thread after a genuine first install (server went from absent to present),
    /// so the View can offer to enable the REST API. Not raised by re-validate / update on an existing install.</summary>
    public event Action? InstallFinished;

    /// <summary>Set by the View: confirm the first install (a multi-GB SteamCMD download) before it starts.
    /// Returns true to proceed. Only gates the Install button, Validate / Download-update aren't multi-GB.</summary>
    public Func<bool>? ConfirmInstall { get; set; }

    /// <summary>Set by the View: when Stop is pressed, ask how to shut down (immediate / timed / force when REST
    /// is off) and return the choice. Keeps the shutdown dialogs in the View, not the ViewModel.</summary>
    public Func<ShutdownDecision>? RequestShutdownDecision { get; set; }

    /// <summary>Set by the View: confirm accelerating a timed shutdown before it skips the countdown.</summary>
    public Func<bool>? ConfirmShutdownNow { get; set; }

    /// <summary>Label for the multi-state primary button (animated dots while busy, so it's clearly not frozen).</summary>
    public string PrimaryActionText => IsBusy
        ? Strings.Vm_Working + new string('.', _busyDots)
        : PrimaryButton.Label(IsInstalled, IsBusy, State, ShutdownRemainingSeconds);

    /// <summary>What the primary button currently represents, drives its color via XAML (Install/Start = green, Stop = red).</summary>
    public PrimaryActionKind PrimaryActionKind => PrimaryButton.Resolve(IsInstalled, IsBusy, State, ShutdownRemainingSeconds);

    /// <summary>Localized display text for the Status tile (the raw ServerState enum, translated).</summary>
    public string StateText => State switch
    {
        ServerState.Stopped => Strings.State_Stopped,
        ServerState.Starting => Strings.State_Starting,
        ServerState.Healthy => Strings.State_Healthy,
        ServerState.Degraded => Strings.State_Degraded,
        ServerState.Zombie => Strings.State_Zombie,
        ServerState.Stopping => Strings.State_Stopping,
        ServerState.Restarting => Strings.State_Restarting,
        ServerState.Backoff => Strings.State_Backoff,
        _ => State.ToString(),
    };

    public MainViewModel(Logger logger, LauncherConfig config)
    {
        _logger = logger;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _config = config;
        InitLeadSlots();
        _controller = new ServerController(_config, _logger);

        _controller.StateChanged += OnControllerStateChanged;
        _controller.HealthUpdated += OnHealthUpdated;
        _controller.NextRestartTextChanged += t => _dispatcher.BeginInvoke(() => NextRestart = t);
        _controller.NextBackupTextChanged += t => _dispatcher.BeginInvoke(() => NextBackup = t);
        _controller.UpdateStatusChanged += t => _dispatcher.BeginInvoke(() => UpdateStatus = t);
        _controller.TimedShutdownChanged += total => _dispatcher.BeginInvoke(() => OnTimedShutdownChanged(total));
        _logger.LineForUi += OnLoggerLine;

        _busyAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _busyAnimationTimer.Tick += (_, _) =>
        {
            _busyDots = (_busyDots + 1) % 4;
            OnPropertyChanged(nameof(PrimaryActionText));
        };

        _forceShutdownRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _forceShutdownRevealTimer.Tick += OnForceShutdownRevealElapsed;

        _shutdownCountdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _shutdownCountdownTimer.Tick += OnShutdownCountdownTick;

        _logger.Info($"Launcher UI ready. Server root: {_config.ServerRoot}");
    }

    // Run the "Working..." dot animation only while a long operation is in progress.
    partial void OnIsBusyChanged(bool value)
    {
        _busyDots = 0;
        _busyAnimationTimer.IsEnabled = value;
        OnPropertyChanged(nameof(PrimaryActionText));
    }

    /// <summary>
    /// Detect an already-running server WITHOUT adopting it (so the View can prompt first), and reflect
    /// install state. Returns the instance count; <see cref="Attach"/> does the actual adopt if the user
    /// reconnects. Called from Loaded.
    /// </summary>
    public int DetectRunningInstances()
    {
        var count = _controller.DetectRunningInstances();
        IsInstalled = _controller.IsInstalled;
        _logger.Debug($"Startup detect. Installed={IsInstalled}, running instances={count}, State={State}");
        return count;
    }

    /// <summary>
    /// Adopt the running server (bind + monitor + REST). Called from the View AFTER the startup prompt, on
    /// reconnect or before a shut-down. Returns true if one was found and adopted.
    /// </summary>
    public bool Attach()
    {
        var alreadyRunning = _controller.Attach();
        IsInstalled = _controller.IsInstalled;
        _logger.Debug($"Attach complete. Installed={IsInstalled}, alreadyRunning={alreadyRunning}, State={State}");
        return alreadyRunning;
    }

    // --- Server lifecycle helpers for the ownership prompts ---
    public bool IsServerRunning => _controller.IsServerRunning;

    // --- REST-API setup prompt (offered whenever the server is installed but REST is off) ---
    /// <summary>Prompt when the server is installed, stopped, and the REST API isn't set up. Asked every launch
    /// until enabled, the launcher is ~90% REST-driven, so declining is a per-session choice, not persisted.
    /// Gated on stopped because enabling writes the ini, which the settings service refuses while running.</summary>
    public bool ShouldPromptRestSetup() => IsInstalled && !IsServerRunning && !_controller.IsRestApiConfigured;

    /// <summary>Enable the REST API with a fresh random admin password (the setup prompt's "Yes").</summary>
    public bool EnableRestApi() => _controller.EnableRestApiWithRandomPassword();

    // --- Settings dialog (opened from the View; edits launch args + PalWorldSettings.ini) ---
    public LauncherConfig Config => _config;
    public Core.GameSettingsService GameSettings => _controller.GameSettings;

    /// <summary>Steam Workshop mod service (the Mods dialog scans / opens the folder / resolves package names through it).</summary>
    public Core.ModService ModService => _controller.ModService;

    /// <summary>Run SteamCMD's interactive sign-in for Workshop downloads. Visible SteamCMD window, the launcher
    /// only passes the username and never sees the password. Returns true on a successful login.</summary>
    public Task<bool> ConnectSteamAsync(string username) => _controller.ConnectSteamAsync(username);

    /// <summary>Check whether SteamCMD still has a cached session for the account (no login window), for the Mods
    /// dialog's sign-in status.</summary>
    public Task<bool> CheckSteamLoginAsync(string username) => _controller.CheckSteamLoginAsync(username);

    /// <summary>Called after the Mods dialog saves. Mods deploy on the next start/restart, so this just notes it.</summary>
    public void ApplyModSettings() => _logger.Info(_config.ModsEnabled
        ? "Mod settings saved. Enabled mods apply on the next server start or restart."
        : "Mods disabled. They'll be turned off on the next server start.");

    /// <summary>Reconnect the Discord bot after its settings changed in the Discord dialog.</summary>
    public void ApplyDiscordSettings() => _controller.ApplyDiscordSettings();
    public int RunningInstanceCount => _controller.RunningInstanceCount;
    public Task ShutdownGracefulAsync() => _controller.StopAsync(graceful: true);
    public Task ForceStopAsync() => _controller.StopAsync(graceful: false);
    public Task StopAllInstancesAsync() => _controller.StopAllInstancesAsync();

    // --- Live server commands (the Server Commands dialog) ---

    /// <summary>Server Commands is live-only: enabled while the server is up (so REST is plausibly usable).</summary>
    public bool CanUseServerCommands => State is ServerState.Healthy or ServerState.Degraded;

    /// <summary>
    /// On every server-state change, manage the Force Shutdown reveal. Entering a transitional state
    /// (Starting / Stopping / Restarting) starts a one-shot countdown, and the button appears only if the
    /// server is still transitional when it elapses. Leaving those states hides it again at once.
    /// </summary>
    partial void OnStateChanged(ServerState value) => RefreshForceShutdownReveal();

    private static bool IsTransitional(ServerState state) =>
        state is ServerState.Starting or ServerState.Stopping or ServerState.Restarting;

    private void RefreshForceShutdownReveal()
    {
        if (IsTransitional(State))
        {
            // Start the countdown once on entering the window and let it run continuously across
            // Starting <-> Stopping <-> Restarting, so the delay measures total time stuck, not per state.
            if (!IsForceShutdownVisible && !_forceShutdownRevealTimer.IsEnabled)
                _forceShutdownRevealTimer.Start();
        }
        else
        {
            _forceShutdownRevealTimer.Stop();
            IsForceShutdownVisible = false;
        }
    }

    private void OnForceShutdownRevealElapsed(object? sender, EventArgs e)
    {
        _forceShutdownRevealTimer.Stop();
        if (IsTransitional(State))
            IsForceShutdownVisible = true;
    }

    /// <summary>Start/stop the display-only timed-shutdown mirror countdown when the controller signals it. The
    /// server runs the authoritative countdown; this ticks a local mirror and turns the button amber.</summary>
    private void OnTimedShutdownChanged(int? total)
    {
        if (total is int seconds)
        {
            ShutdownRemainingSeconds = seconds;
            _shutdownCountdownTimer.Start();
        }
        else
        {
            _shutdownCountdownTimer.Stop();
            ShutdownRemainingSeconds = null;
        }
    }

    private void OnShutdownCountdownTick(object? sender, EventArgs e)
    {
        if (ShutdownRemainingSeconds is int remaining && remaining > 0)
            ShutdownRemainingSeconds = remaining - 1;
        else
            _shutdownCountdownTimer.Stop();
    }

    /// <summary>Immediately kill the server process (direct OS kill, no save). Backs the main-window Force
    /// Shutdown button, the View confirms first.</summary>
    public void ForceShutdownNow() => _controller.ForceShutdownNow();

    /// <summary>Delegate bundle the Server Commands dialog invokes (all route through the controller's REST client).</summary>
    public Core.ServerCommandActions ServerCommands => new(
        GetPlayers: () => _controller.GetPlayersAsync(),
        Announce: message => _controller.AnnounceAsync(message),
        Kick: (userId, reason) => _controller.KickPlayerAsync(userId, reason),
        Ban: (userId, reason) => _controller.BanPlayerAsync(userId, reason),
        Unban: userId => _controller.UnbanPlayerAsync(userId),
        Save: () => _controller.SaveWorldAsync());

    /// <summary>True when a live REST client exists, so a graceful shutdown is possible (else Stop can only force-kill).</summary>
    public bool IsRestApiReady => _controller.RestClient is not null;

    // --- Scheduled-restart settings (persist to launcher.json on change; the scheduler reads config live) ---
    public bool ScheduledRestartEnabled
    {
        get => _config.ScheduledRestartEnabled;
        set { _config.ScheduledRestartEnabled = value; _config.Save(); _controller.RefreshScheduleText(); OnPropertyChanged(); }
    }

    /// <summary>Current restart times (for the times dialog to seed itself).</summary>
    public IReadOnlyList<TimeOnly> RestartTimes => _config.RestartTimes;

    /// <summary>Compact summary shown on the times button, e.g. "6:00 AM, 6:00 PM  +1 more".</summary>
    public string RestartTimesSummary => FormatTimesSummary(_config.RestartTimes);

    /// <summary>Apply times chosen in the dialog: de-dupe, sort, persist, refresh the summary.</summary>
    public void SetRestartTimes(IEnumerable<TimeOnly> times)
    {
        _config.RestartTimes = times.Distinct().OrderBy(t => t).ToList();
        _config.Save();
        _controller.RefreshScheduleText();
        OnPropertyChanged(nameof(RestartTimesSummary));
    }

    private static string FormatTimesSummary(IReadOnlyList<TimeOnly> times)
    {
        if (times.Count == 0)
            return Strings.Vm_TimesSummaryNone;

        // Show up to two times; beyond that, just the first plus a count so the summary stays short enough
        // not to overflow the narrow settings box (it's only a hint, the full list is in the picker).
        if (times.Count <= 2)
            return string.Join(", ", times.Select(t => t.ToString("t", CultureInfo.CurrentCulture)));
        return string.Format(Strings.Vm_TimesSummaryMore, times[0].ToString("t", CultureInfo.CurrentCulture), times.Count - 1);
    }

    public double MinUptimeHours
    {
        get => _config.MinUptimeBeforeRestart.TotalHours;
        set { _config.MinUptimeBeforeRestart = TimeSpan.FromHours(Math.Max(0, value)); _config.Save(); OnPropertyChanged(); }
    }

    // --- Update / broadcast settings (persist to launcher.json on change; the controller reads config live) ---

    /// <summary>"Automatic updates" master (item 1). Derived: on only when a trigger is actually on, so it can't
    /// sit "on" doing nothing, it auto-unchecks when both sub-options are off. Checking it enables both triggers,
    /// unchecking disables both. A pin forces it off.</summary>
    public bool AutomaticUpdatesOn
    {
        get => UpdatePolicy.AnyAutomaticUpdate(_config.VersionPinEnabled, _config.UpdateOnStart, _config.AutoUpdateEnabled);
        set
        {
            _config.UpdateOnStart = value;
            _config.AutoUpdateEnabled = value;
            _config.Save();
            _controller.RefreshUpdateStatusText();
            OnPropertyChanged();
            OnPropertyChanged(nameof(UpdateOnStart));
            OnPropertyChanged(nameof(AutoUpdateEnabled));
        }
    }

    /// <summary>Freeze the server on its current build (item 2a). Captures the installed build id on enable, clears it on
    /// disable, and grays every update control while set.</summary>
    public bool VersionPinEnabled
    {
        get => _config.VersionPinEnabled;
        set
        {
            _config.VersionPinEnabled = value;
            _config.PinnedBuildId = value ? (_controller.InstalledBuildId ?? "") : "";
            _config.Save();
            _controller.RefreshUpdateStatusText();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNotPinned));
            OnPropertyChanged(nameof(CanCheckForUpdate));
            OnPropertyChanged(nameof(PinnedBuildDisplay));
            OnPropertyChanged(nameof(AutomaticUpdatesOn));
            OnPropertyChanged(nameof(UpdateOnStart));
            OnPropertyChanged(nameof(AutoUpdateEnabled));
        }
    }

    /// <summary>Update controls (the master and the two sub-options) are interactable only when no pin is holding
    /// the build. The pin checkbox itself additionally needs an install (<see cref="CanTogglePin"/>).</summary>
    public bool IsNotPinned => !_config.VersionPinEnabled;

    /// <summary>The pin can be toggled once a build is installed (there must be a build to freeze).</summary>
    public bool CanTogglePin => IsInstalled;

    /// <summary>Caption next to the pin: the pinned build id, or empty when unpinned.</summary>
    public string PinnedBuildDisplay =>
        !_config.VersionPinEnabled ? ""
        : _config.PinnedBuildId.Length > 0 ? string.Format(Strings.Main_PinnedBuildFormat, _config.PinnedBuildId)
        : "";

    public bool AutoUpdateEnabled
    {
        get => !_config.VersionPinEnabled && _config.AutoUpdateEnabled; // masked to unchecked while pinned
        set { _config.AutoUpdateEnabled = value; _config.Save(); _controller.RefreshUpdateStatusText(); OnPropertyChanged(); OnPropertyChanged(nameof(AutomaticUpdatesOn)); }
    }

    public bool UpdateOnStart
    {
        get => !_config.VersionPinEnabled && _config.UpdateOnStart;
        set { _config.UpdateOnStart = value; _config.Save(); OnPropertyChanged(); OnPropertyChanged(nameof(AutomaticUpdatesOn)); }
    }

    /// <summary>Compact view: hide the settings sections (Restarts / Backups / Misc) so the log area fills the
    /// window. Persisted, so the choice sticks across launches.</summary>
    public bool CompactMode
    {
        get => _config.CompactMode;
        set
        {
            _config.CompactMode = value;
            _config.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowConfigBoxes));
        }
    }

    /// <summary>Inverse of <see cref="CompactMode"/>, drives the settings-boxes Visibility.</summary>
    public bool ShowConfigBoxes => !_config.CompactMode;

    public bool VerifyOnUpdate
    {
        get => _config.VerifyOnUpdate;
        set { _config.VerifyOnUpdate = value; _config.Save(); OnPropertyChanged(); }
    }

    /// <summary>How often (whole minutes) to poll SteamCMD for a new build while running. Applies on next start.</summary>
    public int UpdateCheckMinutes
    {
        get => (int)_config.UpdateCheckInterval.TotalMinutes;
        set { _config.UpdateCheckInterval = TimeSpan.FromMinutes(Math.Max(1, value)); _config.Save(); OnPropertyChanged(); }
    }

    public bool AnnounceRestartsEnabled
    {
        get => _config.RestartBroadcastEnabled;
        set { _config.RestartBroadcastEnabled = value; _config.Save(); OnPropertyChanged(); }
    }

    public bool ZombieCheckEnabled
    {
        get => _config.ZombieCheckEnabled;
        set { _config.ZombieCheckEnabled = value; _config.Save(); OnPropertyChanged(); }
    }

    public int ZombieFailureThreshold
    {
        get => _config.ZombieFailureThreshold;
        set { _config.ZombieFailureThreshold = Math.Max(1, value); _config.Save(); OnPropertyChanged(); }
    }

    /// <summary>REST health-probe interval in whole seconds (stored as a TimeSpan). Applies on next start.</summary>
    public int HealthProbeSeconds
    {
        get => (int)_config.HealthProbeInterval.TotalSeconds;
        set { _config.HealthProbeInterval = TimeSpan.FromSeconds(Math.Max(1, value)); _config.Save(); OnPropertyChanged(); }
    }

    // --- Backup settings ---
    public bool BackupOnStartup
    {
        get => _config.BackupOnStartup;
        set { _config.BackupOnStartup = value; _config.Save(); OnPropertyChanged(); }
    }

    public bool BackupOnShutdown
    {
        get => _config.BackupOnShutdown;
        set { _config.BackupOnShutdown = value; _config.Save(); OnPropertyChanged(); }
    }

    public bool ScheduledBackupEnabled
    {
        get => _config.ScheduledBackupEnabled;
        set { _config.ScheduledBackupEnabled = value; _config.Save(); _controller.RefreshScheduleText(); OnPropertyChanged(); }
    }

    public int BackupRetentionDays
    {
        get => _config.BackupRetentionDays;
        set { _config.BackupRetentionDays = Math.Max(0, value); _config.Save(); OnPropertyChanged(); }
    }

    /// <summary>Current backup times (for the times dialog to seed itself).</summary>
    public IReadOnlyList<TimeOnly> BackupTimes => _config.BackupTimes;
    public string BackupTimesSummary => FormatTimesSummary(_config.BackupTimes);

    public void SetBackupTimes(IEnumerable<TimeOnly> times)
    {
        _config.BackupTimes = times.Distinct().OrderBy(t => t).ToList();
        _config.Save();
        _controller.RefreshScheduleText();
        OnPropertyChanged(nameof(BackupTimesSummary));
    }

    /// <summary>The up-to-3 announce marks as separate digit-only slots (blank = off). The View gates input
    /// to digits; setting any slot rebuilds the config list (positive, de-duped, largest-first, capped at 3).</summary>
    public string AnnounceLead1 { get => _leadSlots[0]; set => SetLead(0, value); }
    public string AnnounceLead2 { get => _leadSlots[1]; set => SetLead(1, value); }
    public string AnnounceLead3 { get => _leadSlots[2]; set => SetLead(2, value); }

    private void InitLeadSlots()
    {
        var leads = _config.RestartBroadcastLeadMinutes
            .Where(m => m > 0).Distinct().OrderByDescending(m => m).Take(3).ToList();
        for (var i = 0; i < 3; i++)
            _leadSlots[i] = i < leads.Count ? leads[i].ToString() : "";
    }

    private void SetLead(int index, string value)
    {
        _leadSlots[index] = (value ?? "").Trim();
        _config.RestartBroadcastLeadMinutes = _leadSlots
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .OrderByDescending(n => n)
            .Take(3)
            .ToList();
        _config.Save();
    }

    // --- Multi-state primary button: Install -> Start -> Stop ---
    [RelayCommand(CanExecute = nameof(CanPrimaryAction))]
    private Task PrimaryAction()
    {
        var kind = PrimaryButton.Resolve(IsInstalled, IsBusy, State, ShutdownRemainingSeconds);
        _logger.Info($"Button clicked: {kind}");
        return Guard(() => kind switch
        {
            PrimaryActionKind.Install => ConfirmedInstallAsync(),
            PrimaryActionKind.Start => StartCoreAsync(),
            PrimaryActionKind.Stop => StopWithPromptAsync(),
            PrimaryActionKind.ShutdownNow => ShutdownNowAsync(),
            _ => Task.CompletedTask,
        });
    }

    /// <summary>Stop from the primary button: ask the View how to shut down, then route to the controller. The
    /// countdown only ever happens when the user explicitly picks Timed, a plain Stop is immediate.</summary>
    private Task StopWithPromptAsync()
    {
        var decision = RequestShutdownDecision?.Invoke() ?? new ShutdownDecision(ShutdownKind.GracefulNow);
        if (decision.Kind == ShutdownKind.Timed)
        {
            // Fire-and-forget the countdown so THIS command doesn't stay "running" (an AsyncRelayCommand disables
            // itself while executing) for the whole timer. That frees the primary button to re-enable as the amber
            // "Shutdown Now" (accelerate) affordance while the server counts down. Guard still logs any failure.
            _ = Guard(() => _controller.ShutdownWithCountdownAsync(decision.Seconds));
            return Task.CompletedTask;
        }
        return decision.Kind switch
        {
            ShutdownKind.ForceNoRest => _controller.StopAsync(graceful: false),
            ShutdownKind.GracefulNow => _controller.StopAsync(graceful: true),
            _ => Task.CompletedTask, // Cancel
        };
    }

    /// <summary>Accelerate a counting-down timed shutdown from the amber primary button (confirmed in the View).</summary>
    private async Task ShutdownNowAsync()
    {
        if (ConfirmShutdownNow is { } confirm && !confirm())
            return;
        await _controller.ShutdownNowAsync();
    }

    /// <summary>First install pulls several GB via SteamCMD; let the View confirm before we start.</summary>
    private Task ConfirmedInstallAsync()
    {
        if (ConfirmInstall is { } confirm && !confirm())
            return Task.CompletedTask;
        return InstallOrUpdateCoreAsync();
    }

    private bool CanPrimaryAction() => PrimaryButton.CanExecute(IsInstalled, IsBusy, State, ShutdownRemainingSeconds);

    [RelayCommand(CanExecute = nameof(CanRestart))]
    private Task Restart()
    {
        _logger.Info("Button clicked: Restart");
        return Guard(() => _controller.RestartAsync(RestartReason.Manual));
    }

    private bool CanRestart() => !IsBusy && State is ServerState.Healthy or ServerState.Degraded or ServerState.Zombie;

    /// <summary>Validate Files is stopped-only (it re-verifies / can rewrite locked files).</summary>
    public bool UpdateActionsEnabled => IsInstalled && !IsBusy && State == ServerState.Stopped;

    /// <summary>Check for Update is a read-only build-id check, so it's allowed while the server runs too.</summary>
    public bool CanCheckForUpdate => IsInstalled && !IsBusy && !_config.VersionPinEnabled;

    /// <summary>Import an existing server is offered only when no managed server exists yet (it copies one in),
    /// so it's hidden once installed and disabled while busy.</summary>
    public bool CanImport => !IsInstalled && !IsBusy;
    public bool ImportVisible => !IsInstalled;

    // --- External IP + Port Accessibility ---

    /// <summary>The IP:port a player connects to (public IP + game port), or empty until the IP is known.</summary>
    public string ConnectionInfo => string.IsNullOrEmpty(PublicIp) ? "" : $"{PublicIp}:{_config.ServerPort}";

    /// <summary>External IP display: masked dots by default, the real IP:port when revealed, "-" if unknown.</summary>
    public string PublicIpDisplay =>
        string.IsNullOrEmpty(PublicIp) ? "-" : IsIpRevealed ? ConnectionInfo : "••••••••••••";

    public bool CanCopyConnectionInfo => !string.IsNullOrEmpty(PublicIp);

    /// <summary>Port Accessibility is stopped-only (it binds the ports to test them), mirroring Validate Files.</summary>
    public bool CanCheckPorts => !IsBusy && State == ServerState.Stopped;

    /// <summary>Parsed REST / RCON / port values from PalWorldSettings.ini, for the port-check dialog.</summary>
    public PalworldServerSettings ReadServerSettings() => _controller.ReadServerSettings();

    /// <summary>Detect the machine's public IP for the External IP display (best-effort, ignored on failure).</summary>
    public async Task RefreshPublicIpAsync()
    {
        try { PublicIp = await PublicIpLookup.DetectPublicIpAsync() ?? ""; }
        catch (Exception ex) { _logger.Debug($"Public IP detection failed: {ex.Message}"); }
    }

    /// <summary>Validate Files: full SteamCMD integrity pass on the installed (stopped) server.</summary>
    [RelayCommand(CanExecute = nameof(CanValidateFiles))]
    private Task ValidateFiles()
    {
        _logger.Info("Button clicked: Validate Files");
        return Guard(() => InstallOrUpdateCoreAsync(validate: true));
    }

    private bool CanValidateFiles() => UpdateActionsEnabled;

    /// <summary>Check for Update: read-only build-id check. The View prompts to download when one is found.</summary>
    public async Task<(ServerController.UpdateCheckResult Result, string? Latest)> CheckForUpdateAsync()
    {
        IsBusy = true;
        try { return await _controller.CheckForUpdateAsync(); }
        catch (Exception ex) { _logger.Error("Check for update failed", ex); return (ServerController.UpdateCheckResult.CheckFailed, null); }
        finally { IsBusy = false; }
    }

    /// <summary>Download + apply the latest build on a stopped server (after the Check-for-Update prompt).</summary>
    public Task DownloadUpdateAsync() => Guard(() => InstallOrUpdateCoreAsync(validate: false));

    /// <summary>Apply an update via a graceful update-restart (when one is found while the server is running).</summary>
    public Task UpdateAndRestartAsync() => Guard(() => _controller.RestartAsync(RestartReason.Update));

    /// <summary>Take a backup on demand (zip the world + config; fresh /save when the server is up with REST).</summary>
    [RelayCommand(CanExecute = nameof(CanBackupNow))]
    private Task BackupNow()
    {
        _logger.Info("Button clicked: Backup now");
        return Guard(async () =>
        {
            IsBusy = true;
            try { await _controller.BackupNowAsync(); }
            finally { IsBusy = false; }
        });
    }

    private bool CanBackupNow() => IsInstalled && !IsBusy;

    private async Task InstallOrUpdateCoreAsync(bool validate = true)
    {
        var wasInstalled = IsInstalled;
        IsBusy = true;
        try
        {
            await _controller.InstallOrUpdateAsync(validate);
        }
        finally
        {
            IsBusy = false;
            IsInstalled = _controller.IsInstalled;
        }

        // Only after a genuine first install (not a re-validate / update on an existing one).
        if (!wasInstalled && IsInstalled)
            InstallFinished?.Invoke();
    }

    /// <summary>Import an existing (non-launcher) server by copying it into the managed folder. Shows "Working..."
    /// during the copy, then on success fires InstallFinished so the View offers REST setup like a fresh install.</summary>
    public async Task<bool> ImportServerAsync(string sourceDir)
    {
        var wasInstalled = IsInstalled;
        IsBusy = true;
        bool imported;
        try
        {
            imported = await _controller.ImportServerAsync(sourceDir);
        }
        finally
        {
            IsBusy = false;
            IsInstalled = _controller.IsInstalled;
        }
        if (imported && !wasInstalled && IsInstalled)
            InstallFinished?.Invoke();
        return imported;
    }

    /// <summary>Programmatically start the server (used by --start-server on load). Same path as the Start button,
    /// wrapped in Guard so a failure logs instead of crashing.</summary>
    public Task StartServerAsync() => Guard(StartCoreAsync);

    // Start now runs a SteamCMD update check before launching, so it's a long op, show "Working...".
    private async Task StartCoreAsync()
    {
        IsBusy = true;
        try
        {
            await _controller.StartAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Run a command body, logging any failure instead of crashing the app.</summary>
    private async Task Guard(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.Error("Command failed", ex);
        }
    }

    private void OnControllerStateChanged(ServerState state) => _dispatcher.BeginInvoke(() =>
    {
        State = state;
        if (state is ServerState.Stopped or ServerState.Backoff)
            ResetTiles();
    });

    private void OnHealthUpdated(HealthSample s) => _dispatcher.BeginInvoke(() =>
    {
        Version = s.Version;
        Fps = s.Fps;
        Cpu = s.Cpu;
        Players = s.Players;
        Uptime = s.Uptime;
        Memory = s.Memory;
    });

    private void ResetTiles()
    {
        Version = Fps = Cpu = Memory = Players = Uptime = NextRestart = NextBackup = "-";
    }

    // Logger lines arrive on background threads; marshal to the UI before touching the collections.
    private void OnLoggerLine(LogChannel channel, string message) =>
        _dispatcher.BeginInvoke(() => AppendLine(channel, message));

    private const int MaxLogLines = 1000;

    private void AppendLine(LogChannel channel, string message)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {message}";
        AddCapped(LogGeneral, stamped); // General shows everything
        switch (channel)
        {
            case LogChannel.SteamCmd: AddCapped(LogSteamCmd, stamped); break;
            case LogChannel.Server: AddCapped(LogServer, stamped); break;
            case LogChannel.Chat: AddCapped(LogChat, stamped); break;
            case LogChannel.PlayerJoin: AddCapped(LogPlayerJoin, stamped); break;
        }
    }

    private static void AddCapped(ObservableCollection<string> log, string line)
    {
        log.Add(line);
        while (log.Count > MaxLogLines)
            log.RemoveAt(0);
    }
}

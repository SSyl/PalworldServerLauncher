using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;
using PalServerLauncher.Rest.Models;
using PalServerLauncher.State;

namespace PalServerLauncher.Core;

/// <summary>Snapshot of live server stats for the status tiles.</summary>
public sealed record HealthSample(string Version, string Fps, string Players, string Uptime, string Memory, string Cpu);

/// <summary>The state action a metrics reading should drive: fire nothing, flag a failure, mark healthy, or
/// (REST expected but never answered past the boot grace) flag the server as up-but-unreachable.</summary>
public enum ProbeAction { Ignore, Failure, Healthy, Unreachable }

/// <summary>
/// Polls one running server instance (via REST /metrics + /info) to drive state transitions and
/// the status tiles. Promotes Starting -> Healthy once the server responds; flags Degraded then
/// Zombie when the REST API goes unreachable or the simulation freezes (uptime not advancing / fps 0)
/// AFTER it had been healthy. When REST is enabled in the ini but never answers within the boot grace
/// (e.g. a WorldOption.sav overriding the settings, a wrong port, or a password mismatch), it flags
/// RestUnreachable instead of hanging on Starting forever, without triggering recovery (the game is fine).
/// When REST isn't enabled at all it can't read stats, so it just marks the process "running" after a short
/// boot grace so the UI isn't stuck. One monitor per launched process; created/disposed by the controller.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private static readonly TimeSpan NoRestGrace = TimeSpan.FromSeconds(15);

    // REST is enabled in the ini but the server may still be booting. Wait generously before deciding it
    // will never answer, so a slow-booting server isn't falsely flagged (the state self-corrects to Healthy
    // the instant metrics arrive anyway).
    private static readonly TimeSpan RestExpectedGrace = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private readonly Func<PalworldRestClient?> _getRest;
    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _cts = new();

    private DateTime _startedUtc;
    private long _lastUptime = -1;
    private TimeSpan? _lastCpuTotal;      // baseline for computing process CPU% between probes
    private DateTime _lastCpuSampleUtc;
    private int _consecutiveFailures;
    private bool _reachedHealthy;
    private bool _restUnreachableLogged; // one-shot guard so the "REST not responding" warning logs once
    private bool _disposed;

    // Player roster for join/leave logging, keyed by a stable id (userId). Baselined silently on the
    // first read (so adopting a server with players online doesn't log them all as "joined").
    private Dictionary<string, string> _knownPlayers = new();
    private bool _playersBaselined;

    // Optional periodic status line, throttled so it doesn't flood the log at the 7s probe cadence.
    private static readonly TimeSpan StatusLogInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastStatusLogUtc;

    public event Action<ServerState>? StateChanged;
    public event Action<HealthSample>? Sampled;
    public event Action? ZombieDetected;
    /// <summary>A player joined or left (the change and the resulting online count), for Discord notifications.</summary>
    public event Action<RosterChange, int>? PlayerChanged;

    public HealthMonitor(Process process, Func<PalworldRestClient?> getRest, LauncherConfig config, Logger logger)
    {
        _process = process;
        _getRest = getRest;
        _config = config;
        _logger = logger;
    }

    public void Start()
    {
        _startedUtc = DateTime.UtcNow;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_config.HealthProbeInterval);
        try
        {
            // Probe once promptly, then on the interval.
            await ProbeAsync(ct).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await ProbeAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Monitor stopped.
        }
    }

    private async Task ProbeAsync(CancellationToken ct)
    {
        if (_process.HasExited)
            return; // controller handles the Stopped transition via Process.Exited

        var memory = FormatMemory(_process);
        var cpu = SampleCpu(_process);
        var rest = _getRest();

        if (rest is null)
        {
            // No REST API, can't read stats. Treat the process as running after a brief boot grace.
            if (ct.IsCancellationRequested)
                return; // disposed mid-probe, don't fire a stale Healthy / sample
            if (!_reachedHealthy && DateTime.UtcNow - _startedUtc > NoRestGrace)
            {
                _reachedHealthy = true;
                _logger.Info("Server running (REST API disabled, stats unavailable).");
                StateChanged?.Invoke(ServerState.Healthy);
            }
            if (_reachedHealthy)
                Sampled?.Invoke(new HealthSample("REST off", "-", "-", "-", memory, cpu));
            return;
        }

        MetricsResponseSafe? metrics = await SafeMetricsAsync(rest, ct).ConfigureAwait(false);

        // frozen needs the metrics plus the pre-update healthy/uptime state, so compute it before deciding.
        var frozen = metrics is not null && _reachedHealthy && (metrics.Uptime <= _lastUptime || metrics.Fps <= 0);
        var bootGraceElapsed = DateTime.UtcNow - _startedUtc > RestExpectedGrace;
        var action = EvaluateMetricsProbe(ct.IsCancellationRequested, metrics is not null, _reachedHealthy, frozen, bootGraceElapsed);

        // Ignore = fire nothing: either the probe was cancelled by disposal (the swallowed cancellation comes
        // back as a null "failure", see PalworldRestClient) or the server is still booting within the grace.
        if (action == ProbeAction.Ignore)
            return;

        // Unreachable = REST was enabled but never answered past the boot grace, and we never went healthy.
        // Surface it (once) instead of hanging on Starting, but keep probing so a late boot still promotes to
        // Healthy below. Deliberately NOT RegisterFailure: the game server is fine, we must not kill+relaunch it.
        if (action == ProbeAction.Unreachable)
        {
            if (ct.IsCancellationRequested)
                return; // disposed mid-probe, don't fire a stale state / sample
            if (!_restUnreachableLogged)
            {
                _restUnreachableLogged = true;
                _logger.Info($"Server is up but its REST API isn't responding on port {rest.Port}. Check RESTAPIEnabled / RESTAPIPort / AdminPassword, and whether a WorldOption.sav in the save folder is overriding your settings.");
            }
            StateChanged?.Invoke(ServerState.RestUnreachable);
            Sampled?.Invoke(new HealthSample("REST ?", "-", "-", "-", memory, cpu));
            return;
        }

        if (metrics is null)
        {
            RegisterFailure("REST API unreachable", ct); // Failure: unreachable after we'd already been healthy
            return;
        }

        _lastUptime = metrics.Uptime;
        if (!_reachedHealthy)
        {
            _reachedHealthy = true;
            _logger.Info($"Server is up ({metrics.Version}, REST responding).");
        }

        // Re-check right before the raises: disposal may have landed during the bookkeeping above (there is no
        // await here, so this closes all but a sub-instruction window). RegisterFailure re-checks ct on its own.
        if (ct.IsCancellationRequested)
            return;
        if (action == ProbeAction.Failure)
        {
            RegisterFailure($"frozen (uptime={metrics.Uptime}, fps={metrics.Fps})", ct);
        }
        else
        {
            _consecutiveFailures = 0;
            StateChanged?.Invoke(ServerState.Healthy);
        }

        var sample = new HealthSample(
            metrics.Version,
            metrics.Fps.ToString(),
            $"{metrics.Players}/{metrics.MaxPlayers}",
            FormatUptime(metrics.Uptime),
            memory,
            cpu);
        Sampled?.Invoke(sample);

        if (_config.LogHealthStats && DateTime.UtcNow - _lastStatusLogUtc >= StatusLogInterval)
        {
            _lastStatusLogUtc = DateTime.UtcNow;
            _logger.Info($"Status | FPS {sample.Fps} | CPU {sample.Cpu} | Players {sample.Players} | Uptime {sample.Uptime} | Mem {sample.Memory} | {sample.Version}");
        }

        await TrackPlayersAsync(rest, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Read the current roster and log join/leave changes to the Server Log. Palworld has no push for
    /// this, so we diff the roster on each health probe (folded into the existing poll, no extra timer).
    /// The first successful read is baselined silently.
    /// </summary>
    private async Task TrackPlayersAsync(PalworldRestClient rest, CancellationToken ct)
    {
        var response = await rest.GetPlayersAsync(ct).ConfigureAwait(false);
        if (response is null)
            return; // roster unreadable this tick - try again next probe

        var current = new Dictionary<string, string>();
        foreach (var player in response.Players)
        {
            var id = PlayerKey(player);
            if (id is not null)
                current[id] = player.Name ?? player.AccountName ?? id;
        }

        if (!_playersBaselined)
        {
            _knownPlayers = current;
            _playersBaselined = true;
            return;
        }

        foreach (var change in DiffRoster(_knownPlayers, current))
        {
            _logger.PlayerJoin(change.Joined
                ? $"+ {change.Name} [{change.Id}] joined ({current.Count} online)"
                : $"- {change.Name} [{change.Id}] left ({current.Count} online)");
            PlayerChanged?.Invoke(change, current.Count);
        }

        _knownPlayers = current;
    }

    /// <summary>A single roster change: who, and whether they joined (true) or left (false).</summary>
    public readonly record struct RosterChange(string Id, string Name, bool Joined);

    /// <summary>Ids present in <paramref name="current"/> but not <paramref name="previous"/> are joins; the reverse are leaves.</summary>
    public static IReadOnlyList<RosterChange> DiffRoster(
        IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> current)
    {
        var changes = new List<RosterChange>();
        foreach (var (id, name) in current)
            if (!previous.ContainsKey(id))
                changes.Add(new RosterChange(id, name, Joined: true));
        foreach (var (id, name) in previous)
            if (!current.ContainsKey(id))
                changes.Add(new RosterChange(id, name, Joined: false));
        return changes;
    }

    /// <summary>Stable per-player key for diffing: userId (Steam id), else playerId, else name.</summary>
    private static string? PlayerKey(Player player) =>
        !string.IsNullOrWhiteSpace(player.UserId) ? player.UserId
        : !string.IsNullOrWhiteSpace(player.PlayerId) ? player.PlayerId
        : player.Name;

    /// <summary>
    /// Decide what a metrics reading should do. Pure so the disposal-race guard is unit-testable. A cancelled
    /// probe (the monitor was disposed mid-read, which the REST client turns into a null result) always yields
    /// <see cref="ProbeAction.Ignore"/>, so it can never be mistaken for a real failure and fire a spurious
    /// Degraded / Zombie. No metrics after we'd been healthy is a <see cref="ProbeAction.Failure"/>; no metrics
    /// before that means we're still booting -> <see cref="ProbeAction.Ignore"/> until the boot grace elapses,
    /// after which it's <see cref="ProbeAction.Unreachable"/> (REST was expected but never answered) so the UI
    /// stops hanging on Starting. A live-but-frozen reading is a failure too.
    /// </summary>
    public static ProbeAction EvaluateMetricsProbe(bool cancelled, bool hasMetrics, bool reachedHealthy, bool frozen, bool bootGraceElapsed)
    {
        if (cancelled)
            return ProbeAction.Ignore;
        if (!hasMetrics)
            return reachedHealthy ? ProbeAction.Failure
                : bootGraceElapsed ? ProbeAction.Unreachable
                : ProbeAction.Ignore;
        return frozen ? ProbeAction.Failure : ProbeAction.Healthy;
    }

    private void RegisterFailure(string reason, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return; // monitor disposed mid-probe, don't escalate a cancelled read to Degraded / Zombie
        _consecutiveFailures++;
        StateChanged?.Invoke(ServerState.Degraded);
        var threshold = Math.Max(1, _config.ZombieFailureThreshold);
        _logger.Debug($"Health degraded ({_consecutiveFailures}/{threshold}): {reason}");

        if (_config.ZombieCheckEnabled && _consecutiveFailures >= threshold)
        {
            _logger.Error($"Server appears wedged ({reason}), flagged as zombie.");
            StateChanged?.Invoke(ServerState.Zombie);
            _consecutiveFailures = 0;
            ZombieDetected?.Invoke();
        }
    }

    private static async Task<MetricsResponseSafe?> SafeMetricsAsync(PalworldRestClient rest, CancellationToken ct)
    {
        var metrics = await rest.GetMetricsAsync(ct).ConfigureAwait(false);
        if (metrics is null) return null;
        var info = await rest.GetInfoAsync(ct).ConfigureAwait(false);
        return new MetricsResponseSafe(
            info?.Version ?? "-", metrics.ServerFps, metrics.CurrentPlayerNum, metrics.MaxPlayerNum, metrics.Uptime);
    }

    private static string FormatMemory(Process process)
    {
        try
        {
            process.Refresh();
            return MemoryFormat.Format(process.WorkingSet64);
        }
        catch
        {
            return "-";
        }
    }

    /// <summary>Process CPU as a percent of total machine capacity (0-100, all cores), averaged over the
    /// interval since the last probe. Returns "-" on the first sample (no baseline yet) or on error.</summary>
    private string SampleCpu(Process process)
    {
        try
        {
            var now = DateTime.UtcNow;
            var cpuNow = process.TotalProcessorTime;
            var result = "-";
            if (_lastCpuTotal is { } lastCpu)
            {
                var wallSeconds = (now - _lastCpuSampleUtc).TotalSeconds;
                if (wallSeconds > 0)
                {
                    var cores = Math.Max(1, Environment.ProcessorCount);
                    var percent = (cpuNow - lastCpu).TotalSeconds / wallSeconds / cores * 100;
                    result = $"{Math.Clamp(percent, 0, 100):F0}%";
                }
            }
            _lastCpuTotal = cpuNow;
            _lastCpuSampleUtc = now;
            return result;
        }
        catch
        {
            return "-";
        }
    }

    private static string FormatUptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        // Reuse the schedulers' localized H/M and M/S format strings (identical shape) so Uptime matches
        // the "next restart in ..." display instead of showing hardcoded English h/m/s in every language.
        return t.TotalHours >= 1
            ? string.Format(Strings.Sched_UntilHM, (int)t.TotalHours, t.Minutes)
            : string.Format(Strings.Sched_UntilMS, t.Minutes, t.Seconds);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }

    private sealed record MetricsResponseSafe(string Version, int Fps, int Players, int MaxPlayers, long Uptime);
}

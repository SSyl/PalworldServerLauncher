using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;
using PalServerLauncher.Rest.Models;
using PalServerLauncher.State;

namespace PalServerLauncher.Core;

/// <summary>Snapshot of live server stats for the status tiles.</summary>
public sealed record HealthSample(string Version, string Fps, string Players, string Uptime, string Memory);

/// <summary>
/// Polls one running server instance (via REST /metrics + /info) to drive state transitions and
/// the status tiles. Promotes Starting -> Healthy once the server responds; flags Degraded then
/// Zombie when the REST API goes unreachable or the simulation freezes (uptime not advancing / fps 0).
/// When REST isn't enabled it can't read stats, so it just marks the process "running" after a short
/// boot grace so the UI isn't stuck. One monitor per launched process; created/disposed by the controller.
/// </summary>
public sealed class HealthMonitor : IDisposable
{
    private static readonly TimeSpan NoRestGrace = TimeSpan.FromSeconds(15);

    private readonly Process _process;
    private readonly Func<PalworldRestClient?> _getRest;
    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _cts = new();

    private DateTime _startedUtc;
    private long _lastUptime = -1;
    private int _consecutiveFailures;
    private bool _reachedHealthy;
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
        var rest = _getRest();

        if (rest is null)
        {
            // No REST API, can't read stats. Treat the process as running after a brief boot grace.
            if (!_reachedHealthy && DateTime.UtcNow - _startedUtc > NoRestGrace)
            {
                _reachedHealthy = true;
                _logger.Info("Server running (REST API disabled, stats unavailable).");
                StateChanged?.Invoke(ServerState.Healthy);
            }
            if (_reachedHealthy)
                Sampled?.Invoke(new HealthSample("REST off", "-", "-", "-", memory));
            return;
        }

        MetricsResponseSafe? metrics = await SafeMetricsAsync(rest, ct).ConfigureAwait(false);
        if (metrics is null)
        {
            if (_reachedHealthy)
                RegisterFailure("REST API unreachable");
            return; // still booting; unreachable before first healthy sample is expected
        }

        var frozen = _reachedHealthy && (metrics.Uptime <= _lastUptime || metrics.Fps <= 0);
        _lastUptime = metrics.Uptime;

        if (!_reachedHealthy)
        {
            _reachedHealthy = true;
            _logger.Info($"Server is up ({metrics.Version}, REST responding).");
        }

        if (frozen)
        {
            RegisterFailure($"frozen (uptime={metrics.Uptime}, fps={metrics.Fps})");
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
            memory);
        Sampled?.Invoke(sample);

        if (_config.LogHealthStats && DateTime.UtcNow - _lastStatusLogUtc >= StatusLogInterval)
        {
            _lastStatusLogUtc = DateTime.UtcNow;
            _logger.Info($"Status | FPS {sample.Fps} | Players {sample.Players} | Uptime {sample.Uptime} | Mem {sample.Memory} | {sample.Version}");
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
            _logger.Server(change.Joined
                ? $"+ {change.Name} joined ({current.Count} online)"
                : $"- {change.Name} left ({current.Count} online)");
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

    private void RegisterFailure(string reason)
    {
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
            var mb = process.WorkingSet64 / 1024d / 1024d;
            return mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F0} MB";
        }
        catch
        {
            return "-";
        }
    }

    private static string FormatUptime(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
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

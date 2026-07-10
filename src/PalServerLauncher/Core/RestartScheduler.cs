using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// Drives scheduled restarts at explicit times of day. Each 20s tick re-reads the config and, over the
/// window since the last tick, fires (a) any lead-up announcement whose mark (time - lead) just passed and
/// (b) the actual stop+start when the chosen time itself passes - so the shutdown lands ON the chosen time,
/// with warnings leading up to it. Because everything is recomputed from config every tick, editing the
/// times or leads mid-countdown is honored immediately and can't race a captured schedule. A minimum-uptime
/// guard skips (and doesn't announce) a restart on a freshly-started server. Long-lived (owned by the
/// controller): it always reports the next-restart time for the UI, but only acts when enabled and running.
/// Manual/update restarts do NOT go through here - they use the controller's own countdown.
/// </summary>
public sealed class RestartScheduler : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly Func<bool> _isRunning;
    private readonly Func<DateTime?> _serverStartedUtc;
    private readonly Func<int, Task> _announce;
    private readonly Func<Task> _restartNow;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastTick;

    public event Action<string>? NextRestartTextChanged;

    public RestartScheduler(
        LauncherConfig config, Logger logger,
        Func<bool> isRunning, Func<DateTime?> serverStartedUtc,
        Func<int, Task> announce, Func<Task> restartNow)
    {
        _config = config;
        _logger = logger;
        _isRunning = isRunning;
        _serverStartedUtc = serverStartedUtc;
        _announce = announce;
        _restartNow = restartNow;
    }

    public void Start()
    {
        _lastTick = DateTime.Now;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(20));
        try
        {
            Tick();
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick()
    {
        Refresh();

        var now = DateTime.Now;
        if (_config.ScheduledRestartEnabled && _config.RestartTimes.Count > 0 && _isRunning())
        {
            var times = _config.RestartTimes;
            // Re-read leads every tick so a mid-countdown edit is honored; no announcements when off.
            var leads = _config.RestartBroadcastEnabled
                ? _config.RestartBroadcastLeadMinutes
                : (IReadOnlyList<int>)Array.Empty<int>();
            var started = _serverStartedUtc();

            // Warn players as each lead mark (shutdown - lead) is crossed, but only for a restart that
            // will actually fire (never announce a restart we'd then skip for min-uptime).
            foreach (var (shutdown, lead) in DueAnnouncements(_lastTick, now, times, leads))
                if (UptimeReadyBy(shutdown, now, started))
                    _ = _announce(lead);

            // The chosen time IS the shutdown time: stop+start exactly when it's crossed.
            var due = NextRestart(_lastTick, times);
            if (due is not null && due.Value <= now)
            {
                if (UptimeReadyBy(due.Value, now, started))
                {
                    _logger.Info($"Scheduled restart ({due.Value:HH:mm}).");
                    _ = _restartNow();
                }
                else
                {
                    _logger.Info($"Scheduled restart {due.Value:HH:mm} skipped - uptime below minimum ({_config.MinUptimeBeforeRestart.TotalHours:0.#}h).");
                }
            }
        }

        _lastTick = now;
    }

    /// <summary>Whether the server will have met the min-uptime guard by the <paramref name="shutdown"/> moment.</summary>
    private bool UptimeReadyBy(DateTime shutdown, DateTime now, DateTime? startedUtc) =>
        startedUtc.HasValue && (DateTime.UtcNow - startedUtc.Value) + (shutdown - now) >= _config.MinUptimeBeforeRestart;

    /// <summary>
    /// The announcement marks that fell due this tick: for each configured shutdown time and each positive
    /// lead, the mark (shutdown - lead) that lies in (<paramref name="lastTick"/>, <paramref name="now"/>].
    /// Times map to today and tomorrow (same window as <see cref="NextRestart"/>), so a mark can belong to
    /// tomorrow's early-morning shutdown. Pure + edge-detected: each mark fires exactly once, and because
    /// the caller re-reads config every tick, changing the leads or times mid-countdown just changes which
    /// marks are still ahead - past marks never re-fire.
    /// </summary>
    public static IReadOnlyList<(DateTime Shutdown, int Lead)> DueAnnouncements(
        DateTime lastTick, DateTime now, IReadOnlyList<TimeOnly> times, IReadOnlyList<int> leads)
    {
        var due = new List<(DateTime, int)>();
        foreach (var time in times)
            for (var day = 0; day <= 1; day++)
            {
                var shutdown = now.Date.AddDays(day) + time.ToTimeSpan();
                foreach (var lead in leads)
                {
                    if (lead <= 0)
                        continue;
                    var mark = shutdown - TimeSpan.FromMinutes(lead);
                    if (mark > lastTick && mark <= now)
                        due.Add((shutdown, lead));
                }
            }
        return due;
    }

    /// <summary>Recompute and push the next-restart text now (called on tick and after a schedule change).</summary>
    public void Refresh()
    {
        var now = DateTime.Now;
        var hasSchedule = _config.ScheduledRestartEnabled && _config.RestartTimes.Count > 0;
        var next = NextRestart(now, _config.RestartTimes);
        NextRestartTextChanged?.Invoke(
            !hasSchedule ? "off" : !_isRunning() ? "-" : next is null ? "off" : FormatUntil(next.Value - now));
    }

    /// <summary>
    /// The earliest of the configured times-of-day that falls strictly after <paramref name="now"/>
    /// (today or tomorrow), or null if no times are configured.
    /// </summary>
    public static DateTime? NextRestart(DateTime now, IReadOnlyList<TimeOnly> times)
    {
        DateTime? best = null;
        foreach (var time in times)
        {
            for (var day = 0; day <= 1; day++)
            {
                var slot = now.Date.AddDays(day) + time.ToTimeSpan();
                if (slot > now && (best is null || slot < best))
                    best = slot;
            }
        }
        return best;
    }

    private static string FormatUntil(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

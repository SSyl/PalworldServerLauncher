using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// Fires scheduled backups while a server is running, at explicit times of day
/// (<see cref="LauncherConfig.BackupTimes"/>, reusing <see cref="RestartScheduler.NextRestart"/>).
/// Long-lived (owned by the controller); always reports the next-backup time for the UI but only fires
/// when enabled and running.
/// </summary>
public sealed class BackupScheduler : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly Func<bool> _isRunning;
    private readonly Func<Task> _triggerBackup;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastTick;

    public event Action<string>? NextBackupTextChanged;

    public BackupScheduler(LauncherConfig config, Logger logger, Func<bool> isRunning, Func<Task> triggerBackup)
    {
        _config = config;
        _logger = logger;
        _isRunning = isRunning;
        _triggerBackup = triggerBackup;
    }

    public void Start()
    {
        _lastTick = DateTime.Now;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
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
        if (_config.ScheduledBackupEnabled && _config.BackupTimes.Count > 0 && _isRunning()
            && RestartScheduler.NextRestart(_lastTick, _config.BackupTimes) is { } due && due <= now)
        {
            _logger.Info("Scheduled backup (time).");
            _ = _triggerBackup();
        }

        _lastTick = now;
    }

    /// <summary>Recompute and push the next-backup text now (called on tick and after a schedule change).</summary>
    public void Refresh()
    {
        var now = DateTime.Now;
        var enabled = _config.ScheduledBackupEnabled && _config.BackupTimes.Count > 0;
        NextBackupTextChanged?.Invoke(NextBackupText(now, _isRunning(), enabled));
    }

    private string NextBackupText(DateTime now, bool running, bool enabled)
    {
        if (!enabled) return "off";
        if (!running) return "-";
        var soonest = RestartScheduler.NextRestart(now, _config.BackupTimes);
        return soonest is null ? "off" : FormatUntil(soonest.Value - now);
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

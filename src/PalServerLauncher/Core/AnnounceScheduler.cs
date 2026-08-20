using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>
/// Repeats one in-game message on an interval while a server runs. Long-lived (owned by the controller).
/// The anchor resets on every tick the feature is inactive, so the first message lands one full interval
/// after the server comes up, or after the setting is turned on.
/// </summary>
public sealed class AnnounceScheduler : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly Func<bool> _isRunning;
    private readonly Func<Task> _announce;
    private readonly CancellationTokenSource _cts = new();
    private DateTime _anchor;

    public AnnounceScheduler(LauncherConfig config, Func<bool> isRunning, Func<Task> announce)
    {
        _config = config;
        _isRunning = isRunning;
        _announce = announce;
    }

    public void Start()
    {
        _anchor = DateTime.Now;
        _ = LoopAsync(_cts.Token);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var active = _isRunning() && _config.RecurringAnnounceEnabled
            && !string.IsNullOrWhiteSpace(_config.RecurringAnnounceMessage);
        if (!active)
        {
            _anchor = now;
            return;
        }

        var interval = RecurringAnnouncer.Interval(_config.RecurringAnnounceIntervalMinutes);
        if (!RecurringAnnouncer.IsDue(_anchor, now, interval))
            return;

        _anchor = RecurringAnnouncer.Advance(_anchor, now, interval);
        _ = _announce();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

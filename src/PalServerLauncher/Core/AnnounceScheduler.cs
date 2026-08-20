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
    private readonly Func<long> _nowMs;
    private long _anchor;

    internal long AnchorForTests => _anchor;

    /// <param name="nowMs">Monotonic milliseconds. A wall clock would stall the message for the length of
    /// any backward jump, so the default is the one source that cannot jump backwards.</param>
    public AnnounceScheduler(LauncherConfig config, Func<bool> isRunning, Func<Task> announce, Func<long>? nowMs = null)
    {
        _config = config;
        _isRunning = isRunning;
        _announce = announce;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    public void Start()
    {
        Arm();
        _ = LoopAsync(_cts.Token);
    }

    /// <summary>Set the anchor without starting the timer, so a test can drive Tick by hand.</summary>
    internal void Arm() => _anchor = _nowMs();

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

    internal void Tick()
    {
        var now = _nowMs();
        var active = _isRunning() && _config.RecurringAnnounceEnabled
            && !string.IsNullOrWhiteSpace(_config.RecurringAnnounceMessage);
        var interval = RecurringAnnouncer.Interval(_config.RecurringAnnounceIntervalMinutes);
        var (anchor, send) = RecurringAnnouncer.Tick(active, _anchor, now, interval);
        _anchor = anchor;
        if (send)
            _ = _announce();
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

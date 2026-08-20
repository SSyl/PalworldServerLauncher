using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class AnnounceSchedulerTests
{
    private static LauncherConfig Enabled(int intervalMinutes = 60) => new()
    {
        RecurringAnnounceEnabled = true,
        RecurringAnnounceMessage = "Join our Discord",
        RecurringAnnounceIntervalMinutes = intervalMinutes,
    };

    private static AnnounceScheduler Build(LauncherConfig config, Func<long> clock, Func<bool> running, Action onSend) =>
        new(config, running, () => { onSend(); return Task.CompletedTask; }, clock);

    [Fact]
    public void The_message_goes_out_one_interval_after_the_server_starts()
    {
        var now = 5_000L;
        var sends = 0;
        var scheduler = Build(Enabled(), () => now, () => true, () => sends++);
        scheduler.Arm();

        now += (long)TimeSpan.FromMinutes(59).TotalMilliseconds;
        scheduler.Tick();
        Assert.Equal(0, sends);

        now += (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
        scheduler.Tick();
        Assert.Equal(1, sends);
    }

    [Fact]
    public void A_stopped_server_parks_the_anchor_so_the_next_run_waits_a_full_interval()
    {
        var now = 5_000L;
        var sends = 0;
        var running = false;
        var scheduler = Build(Enabled(), () => now, () => running, () => sends++);
        scheduler.Arm();

        // Down for a day. None of that time counts toward the interval.
        now += (long)TimeSpan.FromHours(24).TotalMilliseconds;
        scheduler.Tick();
        running = true;

        now += (long)TimeSpan.FromMinutes(59).TotalMilliseconds;
        scheduler.Tick();
        Assert.Equal(0, sends);

        now += (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
        scheduler.Tick();
        Assert.Equal(1, sends);
    }

    [Fact]
    public void An_interval_longer_than_the_uptime_between_restarts_never_fires()
    {
        // A 3 hour message on a server restarting every 2 hours. The anchor resets at each restart, so the
        // counter never reaches 3 hours. Arithmetic, not a defect, and worth pinning so nobody "fixes" it.
        var now = 5_000L;
        var sends = 0;
        var running = true;
        var scheduler = Build(Enabled(180), () => now, () => running, () => sends++);
        scheduler.Arm();

        for (var cycle = 0; cycle < 12; cycle++)
        {
            for (var quarter = 0; quarter < 8; quarter++)
            {
                now += (long)TimeSpan.FromMinutes(15).TotalMilliseconds;
                scheduler.Tick();
            }
            running = false;                                   // the restart
            now += (long)TimeSpan.FromMinutes(1).TotalMilliseconds;
            scheduler.Tick();
            running = true;
        }

        Assert.Equal(0, sends);
    }

    [Fact]
    public void The_default_clock_is_monotonic_rather_than_wall_time()
    {
        // Environment.TickCount64 is milliseconds since boot. A wall clock in milliseconds is around 1.7e12,
        // orders of magnitude larger, so the two cannot be confused for one another.
        var scheduler = new AnnounceScheduler(Enabled(), () => false, () => Task.CompletedTask);
        scheduler.Arm();

        var wallClockMs = (long)(DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
        Assert.True(Environment.TickCount64 < wallClockMs / 100,
            "this test assumes uptime is far smaller than unix time");
        Assert.True(scheduler.AnchorForTests < wallClockMs / 100,
            "the scheduler anchored on a wall clock, which a backward jump would stall");
    }
}

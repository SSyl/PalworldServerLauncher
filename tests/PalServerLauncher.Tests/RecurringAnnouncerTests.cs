using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class RecurringAnnouncerTests
{
    // Monotonic milliseconds, as Environment.TickCount64 reports them. The value is arbitrary, only
    // the differences matter.
    private const long Anchor = 4_000_000;

    private static long Minutes(double n) => Anchor + (long)TimeSpan.FromMinutes(n).TotalMilliseconds;

    [Theory]
    [InlineData(0, RecurringAnnouncer.MinIntervalMinutes)]
    [InlineData(-5, RecurringAnnouncer.MinIntervalMinutes)]
    [InlineData(60, 60)]
    [InlineData(9999, RecurringAnnouncer.MaxIntervalMinutes)]
    public void Interval_clamps_to_the_bounds(int minutes, int expected) =>
        Assert.Equal(TimeSpan.FromMinutes(expected), RecurringAnnouncer.Interval(minutes));

    [Fact]
    public void IsDue_only_once_the_whole_interval_has_passed()
    {
        var interval = TimeSpan.FromMinutes(60);

        Assert.False(RecurringAnnouncer.IsDue(Anchor, Minutes(59), interval));
        Assert.True(RecurringAnnouncer.IsDue(Anchor, Minutes(60), interval));
        Assert.True(RecurringAnnouncer.IsDue(Anchor, Minutes(61), interval));
    }

    [Fact]
    public void Advance_keeps_the_cadence_when_a_tick_lands_late()
    {
        // Advancing to "now" would push every later message further out, so the anchor lands on the boundary.
        var interval = TimeSpan.FromMinutes(60);

        Assert.Equal(Minutes(60), RecurringAnnouncer.Advance(Anchor, Minutes(60) + 7_000, interval));
    }

    [Fact]
    public void Advance_skips_a_long_gap_instead_of_sending_a_burst()
    {
        // Machine asleep for five hours on a one-hour interval. The next send is one hour out.
        var interval = TimeSpan.FromMinutes(60);
        var now = Minutes(320);

        var advanced = RecurringAnnouncer.Advance(Anchor, now, interval);

        Assert.Equal(Minutes(300), advanced);
        Assert.False(RecurringAnnouncer.IsDue(advanced, now, interval));
    }

    [Fact]
    public void Advance_leaves_the_anchor_alone_when_nothing_is_due()
    {
        var interval = TimeSpan.FromMinutes(60);
        Assert.Equal(Anchor, RecurringAnnouncer.Advance(Anchor, Minutes(30), interval));
    }

    [Fact]
    public void An_inactive_tick_parks_the_anchor_so_the_first_message_waits_a_full_interval()
    {
        // Turning the message on, or starting the server, must not fire one immediately from an anchor
        // that has been ageing while it was off.
        var interval = TimeSpan.FromMinutes(60);

        var parked = RecurringAnnouncer.Tick(active: false, Anchor, Minutes(300), interval);

        Assert.Equal(Minutes(300), parked.Anchor);
        Assert.False(parked.Send);
        Assert.False(RecurringAnnouncer.Tick(active: true, parked.Anchor, Minutes(359), interval).Send);
        Assert.True(RecurringAnnouncer.Tick(active: true, parked.Anchor, Minutes(360), interval).Send);
    }

    [Fact]
    public void An_active_tick_sends_once_per_interval()
    {
        var interval = TimeSpan.FromMinutes(60);

        var first = RecurringAnnouncer.Tick(active: true, Anchor, Minutes(60), interval);
        Assert.True(first.Send);

        Assert.False(RecurringAnnouncer.Tick(active: true, first.Anchor, Minutes(90), interval).Send);
        Assert.True(RecurringAnnouncer.Tick(active: true, first.Anchor, Minutes(120), interval).Send);
    }

    [Fact]
    public void A_sub_millisecond_interval_is_never_due()
    {
        var tick = TimeSpan.FromTicks(1);

        Assert.False(RecurringAnnouncer.IsDue(0, 100, tick));
        Assert.Equal(0, RecurringAnnouncer.Advance(0, 100, tick));
        Assert.False(RecurringAnnouncer.Tick(active: true, 0, 100, tick).Send);
    }
}

using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class RecurringAnnouncerTests
{
    private static readonly DateTime Anchor = new(2026, 8, 19, 12, 0, 0);

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

        Assert.False(RecurringAnnouncer.IsDue(Anchor, Anchor.AddMinutes(59), interval));
        Assert.True(RecurringAnnouncer.IsDue(Anchor, Anchor.AddMinutes(60), interval));
        Assert.True(RecurringAnnouncer.IsDue(Anchor, Anchor.AddMinutes(61), interval));
    }

    [Fact]
    public void Advance_keeps_the_cadence_when_a_tick_lands_late()
    {
        // Advancing to "now" would push every later message further out, so the anchor lands on the boundary.
        var interval = TimeSpan.FromMinutes(60);
        var late = Anchor.AddMinutes(60).AddSeconds(7);

        Assert.Equal(Anchor.AddMinutes(60), RecurringAnnouncer.Advance(Anchor, late, interval));
    }

    [Fact]
    public void Advance_skips_a_long_gap_instead_of_sending_a_burst()
    {
        // Machine asleep for five hours on a one-hour interval. The next send is one hour out.
        var interval = TimeSpan.FromMinutes(60);
        var now = Anchor.AddHours(5).AddMinutes(20);

        var advanced = RecurringAnnouncer.Advance(Anchor, now, interval);

        Assert.Equal(Anchor.AddHours(5), advanced);
        Assert.False(RecurringAnnouncer.IsDue(advanced, now, interval));
    }

    [Fact]
    public void Advance_leaves_the_anchor_alone_when_nothing_is_due()
    {
        var interval = TimeSpan.FromMinutes(60);
        Assert.Equal(Anchor, RecurringAnnouncer.Advance(Anchor, Anchor.AddMinutes(30), interval));
    }
}

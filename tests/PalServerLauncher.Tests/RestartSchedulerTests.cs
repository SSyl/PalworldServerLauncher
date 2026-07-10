using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class RestartSchedulerTests
{
    [Fact]
    public void SingleTime_before_it_returns_today()
    {
        var now = new DateTime(2026, 7, 8, 3, 0, 0);
        var next = RestartScheduler.NextRestart(now, new[] { new TimeOnly(6, 0) });
        Assert.Equal(new DateTime(2026, 7, 8, 6, 0, 0), next);
    }

    [Fact]
    public void SingleTime_after_it_returns_tomorrow()
    {
        var now = new DateTime(2026, 7, 8, 9, 0, 0);
        var next = RestartScheduler.NextRestart(now, new[] { new TimeOnly(6, 0) });
        Assert.Equal(new DateTime(2026, 7, 9, 6, 0, 0), next);
    }

    [Fact]
    public void MultipleTimes_picks_the_nearest_upcoming()
    {
        // Restart at 06:00 and 18:00; at 11:00 the next is 18:00 today.
        var times = new[] { new TimeOnly(6, 0), new TimeOnly(18, 0) };
        var next = RestartScheduler.NextRestart(new DateTime(2026, 7, 8, 11, 0, 0), times);
        Assert.Equal(new DateTime(2026, 7, 8, 18, 0, 0), next);
    }

    [Fact]
    public void MultipleTimes_after_last_wraps_to_first_tomorrow()
    {
        var times = new[] { new TimeOnly(6, 0), new TimeOnly(18, 0) };
        var next = RestartScheduler.NextRestart(new DateTime(2026, 7, 8, 23, 0, 0), times);
        Assert.Equal(new DateTime(2026, 7, 9, 6, 0, 0), next);
    }

    [Fact]
    public void Unordered_times_still_pick_nearest()
    {
        // Order in the list shouldn't matter.
        var times = new[] { new TimeOnly(18, 0), new TimeOnly(6, 0) };
        var next = RestartScheduler.NextRestart(new DateTime(2026, 7, 8, 5, 0, 0), times);
        Assert.Equal(new DateTime(2026, 7, 8, 6, 0, 0), next);
    }

    [Fact]
    public void Exactly_on_a_time_returns_the_next_occurrence()
    {
        var next = RestartScheduler.NextRestart(new DateTime(2026, 7, 8, 6, 0, 0), new[] { new TimeOnly(6, 0) });
        Assert.Equal(new DateTime(2026, 7, 9, 6, 0, 0), next);
    }

    [Fact]
    public void NoTimes_returns_null()
    {
        var next = RestartScheduler.NextRestart(new DateTime(2026, 7, 8, 6, 0, 0), Array.Empty<TimeOnly>());
        Assert.Null(next);
    }

    // --- DueAnnouncements: the lead-up warning marks (shutdown - lead) that crossed this tick ---

    private static readonly TimeOnly Noon = new(12, 0);

    [Fact]
    public void DueAnnouncements_fires_each_mark_in_its_own_tick()
    {
        var times = new[] { Noon };
        var leads = new[] { 30, 15, 5 };

        // The 30m mark (11:30) is due only in the tick that spans 11:30.
        var at1130 = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 11, 29, 50), new DateTime(2026, 7, 8, 11, 30, 10), times, leads);
        Assert.Equal(new[] { (new DateTime(2026, 7, 8, 12, 0, 0), 30) }, at1130);

        // The 5m mark (11:55) only in the tick that spans 11:55.
        var at1155 = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 11, 54, 50), new DateTime(2026, 7, 8, 11, 55, 10), times, leads);
        Assert.Equal(new[] { (new DateTime(2026, 7, 8, 12, 0, 0), 5) }, at1155);
    }

    [Fact]
    public void DueAnnouncements_nothing_after_the_restart()
    {
        var at1220 = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 12, 19, 50), new DateTime(2026, 7, 8, 12, 20, 10),
            new[] { Noon }, new[] { 30, 15, 5 });
        Assert.Empty(at1220);
    }

    [Fact]
    public void DueAnnouncements_editing_leads_midcountdown_does_not_refire_a_passed_mark()
    {
        // At 11:55 the leads change 30/15/5 -> 15/5/1. In the next tick (11:55:00 -> 11:55:20) with the new
        // leads nothing fires: 15m/5m are at/behind lastTick, the 1m is still ahead - no double-warning.
        var justAfterEdit = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 11, 55, 0), new DateTime(2026, 7, 8, 11, 55, 20),
            new[] { Noon }, new[] { 15, 5, 1 });
        Assert.Empty(justAfterEdit);

        // The new 1m mark fires only when 11:59 is crossed.
        var at1159 = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 11, 58, 50), new DateTime(2026, 7, 8, 11, 59, 10),
            new[] { Noon }, new[] { 15, 5, 1 });
        Assert.Equal(new[] { (new DateTime(2026, 7, 8, 12, 0, 0), 1) }, at1159);
    }

    [Fact]
    public void DueAnnouncements_time_change_moves_the_marks()
    {
        var times = new[] { new TimeOnly(12, 5) };
        var leads = new[] { 5 };

        // For a 12:05 restart the 5m mark is at 12:00, not 11:55.
        Assert.Empty(RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 11, 54, 50), new DateTime(2026, 7, 8, 11, 55, 10), times, leads));
        Assert.Equal(new[] { (new DateTime(2026, 7, 8, 12, 5, 0), 5) },
            RestartScheduler.DueAnnouncements(
                new DateTime(2026, 7, 8, 11, 59, 50), new DateTime(2026, 7, 8, 12, 0, 10), times, leads));
    }

    [Fact]
    public void DueAnnouncements_mark_can_belong_to_tomorrows_early_shutdown()
    {
        // Shutdown 00:30 with a 45m lead -> mark at 23:45 the previous evening (from the "tomorrow" candidate).
        var due = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 23, 44, 55), new DateTime(2026, 7, 8, 23, 45, 5),
            new[] { new TimeOnly(0, 30) }, new[] { 45 });
        Assert.Equal(new[] { (new DateTime(2026, 7, 9, 0, 30, 0), 45) }, due);
    }

    [Fact]
    public void DueAnnouncements_ignores_non_positive_leads()
    {
        var due = RestartScheduler.DueAnnouncements(
            new DateTime(2026, 7, 8, 11, 54, 50), new DateTime(2026, 7, 8, 11, 55, 10),
            new[] { Noon }, new[] { 0, 5 });
        Assert.Equal(new[] { (new DateTime(2026, 7, 8, 12, 0, 0), 5) }, due);
    }
}

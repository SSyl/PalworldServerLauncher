namespace PalServerLauncher.Core;

/// <summary>
/// Interval math for the repeating in-game message. The anchor is the last send, and a due tick advances
/// it by whole intervals rather than to "now", so a late tick can't push every later message further out.
/// A gap longer than the interval (a slept machine, an idle server) sends once, never a catch-up burst.
/// </summary>
public static class RecurringAnnouncer
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;

    /// <summary>The configured minutes as an interval, clamped to the bounds the dialog enforces.</summary>
    public static TimeSpan Interval(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes));

    public static bool IsDue(DateTime anchor, DateTime now, TimeSpan interval) =>
        interval > TimeSpan.Zero && now - anchor >= interval;

    /// <summary>The anchor after a send: the newest interval boundary at or before <paramref name="now"/>.</summary>
    public static DateTime Advance(DateTime anchor, DateTime now, TimeSpan interval)
    {
        if (!IsDue(anchor, now, interval))
            return anchor;
        var steps = (long)((now - anchor).Ticks / interval.Ticks);
        return anchor + TimeSpan.FromTicks(interval.Ticks * steps);
    }
}

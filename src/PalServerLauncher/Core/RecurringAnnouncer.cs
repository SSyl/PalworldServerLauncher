namespace PalServerLauncher.Core;

/// <summary>
/// Interval math for the repeating in-game message, in monotonic milliseconds. A wall clock would stall
/// the message for the length of any backward jump, DST included. The anchor is the last send, and a due
/// tick advances it by whole intervals rather than to "now", so a late tick can't push every later message
/// further out and a long gap sends once instead of a catch-up burst.
/// </summary>
public static class RecurringAnnouncer
{
    public const int MinIntervalMinutes = 1;
    public const int MaxIntervalMinutes = 1440;

    /// <summary>The configured minutes as an interval, clamped to the bounds the dialog enforces.</summary>
    public static TimeSpan Interval(int minutes) =>
        TimeSpan.FromMinutes(Math.Clamp(minutes, MinIntervalMinutes, MaxIntervalMinutes));

    /// <summary>One tick's decision: the anchor to keep, and whether to send now. An inactive tick parks the
    /// anchor at now, so enabling the message or starting the server waits a full interval for the first one.</summary>
    public static (long Anchor, bool Send) Tick(bool active, long anchorMs, long nowMs, TimeSpan interval)
    {
        if (!active)
            return (nowMs, false);
        if (!IsDue(anchorMs, nowMs, interval))
            return (anchorMs, false);
        return (Advance(anchorMs, nowMs, interval), true);
    }

    public static bool IsDue(long anchorMs, long nowMs, TimeSpan interval) =>
        // Whole milliseconds, so a sub-millisecond interval is never due rather than dividing by zero below.
        (long)interval.TotalMilliseconds > 0 && nowMs - anchorMs >= (long)interval.TotalMilliseconds;

    /// <summary>The anchor after a send, the newest interval boundary at or before <paramref name="nowMs"/>.</summary>
    public static long Advance(long anchorMs, long nowMs, TimeSpan interval)
    {
        if (!IsDue(anchorMs, nowMs, interval))
            return anchorMs;
        var step = (long)interval.TotalMilliseconds;
        return anchorMs + step * ((nowMs - anchorMs) / step);
    }
}

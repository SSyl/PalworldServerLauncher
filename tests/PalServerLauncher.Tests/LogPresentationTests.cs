using System.Globalization;
using PalServerLauncher;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

/// <summary>
/// How a line is tagged and colored. The tag is shared with the log file, so a change here changes both.
/// </summary>
public class LogPresentationTests
{
    [Theory]
    [InlineData(LogChannel.General, LogLevel.Info, "INFO")]
    [InlineData(LogChannel.General, LogLevel.Debug, "DEBUG")]
    [InlineData(LogChannel.General, LogLevel.Error, "ERROR")]
    [InlineData(LogChannel.SteamCmd, LogLevel.Info, "STEAM")]
    [InlineData(LogChannel.Server, LogLevel.Info, "SERVER")]
    [InlineData(LogChannel.Chat, LogLevel.Info, "CHAT")]
    [InlineData(LogChannel.PlayerJoin, LogLevel.Info, "PLAYER")]
    public void Every_channel_and_level_keeps_the_tag_the_log_file_has_always_used(
        LogChannel channel, LogLevel level, string expected)
    {
        Assert.Equal(expected, LogEntry.TagFor(channel, level));
    }

    [Fact]
    public void The_tag_column_is_padded_so_the_text_lines_up()
    {
        var entry = new LogEntry(new DateTime(2026, 8, 12, 14, 57, 39), LogChannel.General, LogLevel.Info, "ready");

        Assert.Equal("[14:57:39] [INFO]   ready", entry.Render(withDate: false, withTag: true));
        Assert.Equal("[14:57:39] ready", entry.Render(withDate: false, withTag: false));
    }

    [Fact]
    public void The_date_toggle_only_changes_the_time_column()
    {
        var entry = new LogEntry(new DateTime(2026, 8, 12, 14, 57, 39), LogChannel.Chat, LogLevel.Info, "<SSyl> hi");

        Assert.Equal("[2026-08-12 14:57:39] <SSyl> hi", entry.Render(withDate: true, withTag: false));
        Assert.Equal("[2026-08-12 14:57:39] [CHAT]   <SSyl> hi", entry.Render(withDate: true, withTag: true));
    }

    [Fact]
    public void The_date_renders_in_the_gregorian_calendar_whatever_the_regional_setting()
    {
        // Interpolation formats with CurrentCulture, so a Thai regional setting renders 2569 for 2026 and an
        // Arabic one renders 1448. The log file, the filename, and LogLinePrefix all assume Gregorian.
        var entry = new LogEntry(new DateTime(2026, 8, 12, 14, 57, 39), LogChannel.General, LogLevel.Info, "ready");
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal("[2026-08-12 14:57:39] ready", entry.Render(withDate: true, withTag: false));

            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            Assert.Equal("[2026-08-12 14:57:39] ready", entry.Render(withDate: true, withTag: false));

            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");
            Assert.Equal("[14:57:39] ready", entry.Render(withDate: false, withTag: false));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(LogChannel.General, LogLevel.Info)]
    [InlineData(LogChannel.General, LogLevel.Debug)]
    [InlineData(LogChannel.General, LogLevel.Error)]
    [InlineData(LogChannel.SteamCmd, LogLevel.Info)]
    [InlineData(LogChannel.Server, LogLevel.Info)]
    [InlineData(LogChannel.Chat, LogLevel.Info)]
    [InlineData(LogChannel.PlayerJoin, LogLevel.Info)]
    public void Every_tag_column_is_the_same_width(LogChannel channel, LogLevel level)
    {
        // The message column only lines up if no tag overflows the padding. SERVER and PLAYER are exactly 8.
        Assert.Equal(8, LogEntry.TagColumn(channel, level).Length);
    }

    [Fact]
    public void Errors_are_the_only_launcher_lines_that_change_color()
    {
        Assert.Same(Theme.Error, Theme.ForLog(LogChannel.General, LogLevel.Error));
        Assert.Same(Theme.LogText, Theme.ForLog(LogChannel.General, LogLevel.Info));
    }

    [Fact]
    public void Bulk_output_recedes_and_player_activity_stands_out()
    {
        // Server and SteamCMD were 134 of 249 lines in a real session log, so they are the noise floor.
        Assert.Same(Theme.LogDim, Theme.ForLog(LogChannel.Server, LogLevel.Info));
        Assert.Same(Theme.LogDim, Theme.ForLog(LogChannel.SteamCmd, LogLevel.Info));
        Assert.Same(Theme.LogDim, Theme.ForLog(LogChannel.General, LogLevel.Debug));

        Assert.Same(Theme.LogEvent, Theme.ForLog(LogChannel.Chat, LogLevel.Info));
        Assert.Same(Theme.LogEvent, Theme.ForLog(LogChannel.PlayerJoin, LogLevel.Info));
    }
}

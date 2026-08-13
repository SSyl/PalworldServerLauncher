using System.Globalization;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

/// <summary>
/// What the file gets against what the tabs get. The file keeps every line verbatim, the tabs get one timestamp
/// and no source markers, and some lines earn a file entry but not a row.
/// </summary>
public class LogEmitTests
{
    private static readonly DateTime Read = new(2026, 8, 12, 14, 57, 39, 123);

    [Fact]
    public void The_file_line_keeps_the_source_stamp_the_tab_drops()
    {
        const string raw = "[2026-08-12 14:57:30] Client version: 1785799152";

        Assert.Equal("[2026-08-12 14:57:39.123] [STEAM]  " + raw,
            Logger.FileLine(Read, LogChannel.SteamCmd, LogLevel.Info, raw));
        Assert.Equal("Client version: 1785799152",
            Logger.UiEntry(Read, LogChannel.SteamCmd, LogLevel.Info, raw)!.Text);
    }

    [Fact]
    public void A_palworld_line_is_stamped_when_it_happened_not_when_we_read_it()
    {
        // Read at 14:57:39, written by Palworld at 14:56:36. The 63 second gap is real, measured on a live server.
        var entry = Logger.UiEntry(Read, LogChannel.Server, LogLevel.Info,
            "[2026-08-12 14:56:36] [LOG] SSyl joined the server.");

        Assert.Equal(new DateTime(2026, 8, 12, 14, 56, 36), entry!.Time);
    }

    [Fact]
    public void A_line_with_no_source_stamp_falls_back_to_when_we_read_it()
    {
        var entry = Logger.UiEntry(Read, LogChannel.Server, LogLevel.Info, "REST API started on port 8212");

        Assert.Equal(Read, entry!.Time);
    }

    [Fact]
    public void Chat_markers_come_off_only_on_the_chat_channel()
    {
        Assert.Equal("<SSyl> hi",
            Logger.UiEntry(Read, LogChannel.Chat, LogLevel.Info, "[CHAT] <SSyl> hi")!.Text);

        // The same text arriving as server output keeps its marker, since only chat lines are known to carry one.
        Assert.Equal("[CHAT] <SSyl> hi",
            Logger.UiEntry(Read, LogChannel.Server, LogLevel.Info, "[CHAT] <SSyl> hi")!.Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[2026-08-12 14:57:42]")]
    public void A_line_with_nothing_left_after_the_stamp_gets_no_row(string raw)
    {
        // SteamCMD emits both blank lines and timestamp-only lines. They earn a file entry but not a row.
        Assert.Null(Logger.UiEntry(Read, LogChannel.SteamCmd, LogLevel.Info, raw));
        Assert.Equal($"[2026-08-12 14:57:39.123] [STEAM]  {raw}",
            Logger.FileLine(Read, LogChannel.SteamCmd, LogLevel.Info, raw));
    }

    [Theory]
    [InlineData("th-TH")]  // Buddhist calendar, renders 2026 as 2569
    [InlineData("ar-SA")]  // Umm al-Qura calendar, renders 2026 as 1448
    [InlineData("fi-FI")]  // swaps the time separator, rendering 14:57:39 as 14.57.39
    public void The_file_line_uses_the_gregorian_calendar_whatever_the_regional_setting(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("[2026-08-12 14:57:39.123] [INFO]   ready",
                Logger.FileLine(Read, LogChannel.General, LogLevel.Info, "ready"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

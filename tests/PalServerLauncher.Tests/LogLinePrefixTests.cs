using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

/// <summary>
/// Splitting a source's own timestamp off a captured line. Real samples, taken from a live server log.
/// </summary>
public class LogLinePrefixTests
{
    [Fact]
    public void SteamCmd_keeps_its_time_and_loses_the_prefix()
    {
        var (time, rest) = LogLinePrefix.Split("[2026-08-12 14:57:39] Client version: 1785799152");

        Assert.Equal(new DateTime(2026, 8, 12, 14, 57, 39), time);
        Assert.Equal("Client version: 1785799152", rest);
    }

    [Fact]
    public void Palworld_player_lines_keep_the_time_they_actually_happened()
    {
        // This is the one that matters: we read this line at 22:35:58, 63 seconds after Palworld wrote it.
        var (time, rest) = LogLinePrefix.Split(
            "[2026-08-11 22:34:55] [LOG] SSyl joined the server. (User id: steam_76561197961265085)");

        Assert.Equal(new DateTime(2026, 8, 11, 22, 34, 55), time);
        Assert.StartsWith("[LOG] SSyl joined", rest);
    }

    [Fact]
    public void Unreal_lines_lose_the_prefix_and_the_frame_counter_without_keeping_the_time()
    {
        // Unreal stamps UTC, which read five hours ahead of every other line on the same server.
        var (time, rest) = LogLinePrefix.Split(
            "[2026.08.12-19.57.56:668][  0]LogMemory: Platform Memory Stats for WindowsServer");

        Assert.Null(time);
        Assert.Equal("LogMemory: Platform Memory Stats for WindowsServer", rest);
    }

    [Fact]
    public void A_line_that_is_only_a_timestamp_is_left_empty()
    {
        var (time, rest) = LogLinePrefix.Split("[2026-08-12 14:57:42]");

        Assert.Equal(new DateTime(2026, 8, 12, 14, 57, 42), time);
        Assert.Equal("", rest);
    }

    [Theory]
    // Real PalDefender 1.8.3 lines. Its clock goes and its level stays, since the level is all that
    // separates a cheat detection from a crafting line.
    [InlineData("[04:22:56][warning] 'SSyl' (UserId=steam_765, IP=127.0.0.1) is a cheater! Reason: used /imcheater command",
                "[warning] 'SSyl' (UserId=steam_765, IP=127.0.0.1) is a cheater! Reason: used /imcheater command")]
    [InlineData("[04:21:18][info] PalDefender Anti Cheat v1.8.3 loaded!", "[info] PalDefender Anti Cheat v1.8.3 loaded!")]
    [InlineData("[04:07:10][d3d9][info] Loading 'PalDefender.dll'...", "[d3d9][info] Loading 'PalDefender.dll'...")]
    public void A_bare_clock_is_dropped_because_it_carries_no_date(string line, string expected)
    {
        var (time, rest) = LogLinePrefix.Split(line);

        Assert.Null(time);
        Assert.Equal(expected, rest);
    }

    [Theory]
    [InlineData("Setting breakpad minidump AppID = 1623730")]
    [InlineData("REST API started on port 8212")]
    [InlineData("")]
    [InlineData("[not a timestamp] text")]
    [InlineData("[2026-08-12 14:57:42 unterminated")]
    [InlineData("[24:00:00] out of range")]
    [InlineData("[4:22:56] not zero padded")]
    public void Anything_else_is_returned_untouched(string line)
    {
        var (time, rest) = LogLinePrefix.Split(line);

        Assert.Null(time);
        Assert.Equal(line, rest);
    }

    [Fact]
    public void Vanilla_chat_loses_its_marker()
    {
        Assert.Equal("<SSyl> Testing", LogLinePrefix.StripChatMarkers("[CHAT] <SSyl> Testing"));
    }

    [Theory]
    [InlineData("Global")]
    [InlineData("Local")]
    [InlineData("Guild")]
    public void PalDefender_chat_keeps_its_channel_and_loses_its_time_and_level(string scope)
    {
        // Vanilla Palworld marks no channel at all, so the one thing PalDefender adds has to survive.
        var line = $"[20:03:50][info] [Chat::{scope}]['SSyl' (UserId=abc, IP=192.168.50.1)]: Test";

        Assert.Equal($"[{scope}] ['SSyl' (UserId=abc, IP=192.168.50.1)]: Test",
            LogLinePrefix.StripChatMarkers(line));
    }

    [Theory]
    [InlineData("info")]
    [InlineData("warn")]
    [InlineData("warning")]  // spdlog's own spelling
    [InlineData("error")]
    public void Every_level_spelling_a_source_might_use_is_stripped_from_chat(string level)
    {
        Assert.Equal("[Global] ['SSyl' (UserId=abc)]: Test",
            LogLinePrefix.StripChatMarkers($"[20:03:50][{level}] [Chat::Global]['SSyl' (UserId=abc)]: Test"));
    }

    [Fact]
    public void The_bracketed_speaker_survives_the_scan()
    {
        // The scan stops at the first bracket it doesn't recognize, which is what protects the player identity.
        Assert.Equal("[Global] ['info' (UserId=abc)]: hi",
            LogLinePrefix.StripChatMarkers("[Chat::Global]['info' (UserId=abc)]: hi"));
    }

    [Fact]
    public void A_player_cannot_strip_their_own_message_by_naming_themselves_a_marker()
    {
        // A message body is never scanned, only the leading brackets, so a typed "[CHAT]" stays typed.
        Assert.Equal("<SSyl> [CHAT] [Chat::Global] hi",
            LogLinePrefix.StripChatMarkers("[CHAT] <SSyl> [CHAT] [Chat::Global] hi"));
    }

    [Fact]
    public void An_empty_channel_is_dropped_rather_than_rendered_as_empty_brackets()
    {
        Assert.Equal("['SSyl']: hi", LogLinePrefix.StripChatMarkers("[Chat::]['SSyl']: hi"));
    }

    [Fact]
    public void A_chat_line_with_no_marker_is_untouched()
    {
        Assert.Equal("<SSyl> Testing", LogLinePrefix.StripChatMarkers("<SSyl> Testing"));
    }

    [Fact]
    public void A_player_cannot_forge_a_timestamp_from_inside_a_chat_message()
    {
        // Chat text is player-authored. The split is anchored at position 0, which is always Palworld's own
        // prefix, so a message body that looks like a stamp stays part of the message.
        var (time, rest) = LogLinePrefix.Split("[2026-08-12 13:43:40] [CHAT] <SSyl> [2020-01-01 00:00:00] hi");

        Assert.Equal(new DateTime(2026, 8, 12, 13, 43, 40), time);
        Assert.Equal("[CHAT] <SSyl> [2020-01-01 00:00:00] hi", rest);
    }
}

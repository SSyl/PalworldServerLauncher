using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

/// <summary>
/// The log filter box's matcher. Token-AND over the line's text and its source tag.
/// </summary>
public class LogSearchTests
{
    private static LogEntry Entry(LogChannel channel, LogLevel level, string text) =>
        new(new DateTime(2026, 8, 13, 14, 57, 39), channel, level, text);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_query_matches_everything(string? query)
    {
        Assert.True(LogSearch.Matches(query, Entry(LogChannel.General, LogLevel.Info, "anything at all")));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var entry = Entry(LogChannel.General, LogLevel.Info, "Backup complete");

        Assert.True(LogSearch.Matches("BACKUP", entry));
        Assert.True(LogSearch.Matches("backup", entry));
        Assert.True(LogSearch.Matches("bAcKuP", entry));
    }

    [Fact]
    public void A_substring_matches_mid_word()
    {
        Assert.True(LogSearch.Matches("ackup", Entry(LogChannel.General, LogLevel.Info, "Backup complete")));
    }

    [Fact]
    public void Every_token_has_to_appear_but_not_in_order_or_adjacent()
    {
        var entry = Entry(LogChannel.SteamCmd, LogLevel.Info, "Update state (0x61) downloading, progress: 42.10");

        Assert.True(LogSearch.Matches("update progress", entry));
        Assert.True(LogSearch.Matches("progress update", entry));
        Assert.False(LogSearch.Matches("update finished", entry));
    }

    [Fact]
    public void The_source_tag_is_searchable_so_a_channel_can_be_filtered_by_name()
    {
        Assert.True(LogSearch.Matches("chat", Entry(LogChannel.Chat, LogLevel.Info, "hello world")));
        Assert.True(LogSearch.Matches("player", Entry(LogChannel.PlayerJoin, LogLevel.Info, "Syl joined")));
        Assert.True(LogSearch.Matches("error", Entry(LogChannel.General, LogLevel.Error, "could not write the file")));
        Assert.False(LogSearch.Matches("error", Entry(LogChannel.General, LogLevel.Info, "could not write the file")));
    }

    [Fact]
    public void A_token_cannot_span_the_boundary_between_the_tag_and_the_text()
    {
        // The separator keeps "CHATa" from matching a CHAT line whose text starts with 'a'.
        Assert.False(LogSearch.Matches("chatabc", Entry(LogChannel.Chat, LogLevel.Info, "abc")));
    }

    [Fact]
    public void A_query_with_no_spaces_matches_as_one_substring()
    {
        // CJK carries no word breaks, so the whole query is a single token by definition.
        Assert.True(LogSearch.Matches("備份", Entry(LogChannel.General, LogLevel.Info, "備份完成")));
        Assert.False(LogSearch.Matches("重啟", Entry(LogChannel.General, LogLevel.Info, "備份完成")));
    }

    [Fact]
    public void Surrounding_whitespace_in_the_query_is_ignored()
    {
        Assert.True(LogSearch.Matches("  backup  ", Entry(LogChannel.General, LogLevel.Info, "Backup complete")));
    }
}

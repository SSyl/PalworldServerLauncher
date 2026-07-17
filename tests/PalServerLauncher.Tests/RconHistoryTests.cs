using System.Collections.Generic;
using System.Linq;
using PalServerLauncher.Rcon;

namespace PalServerLauncher.Tests;

public class RconHistoryTests
{
    [Fact]
    public void Add_puts_the_newest_command_first()
    {
        var history = RconHistory.Add(new List<string> { "Info" }, "ShowPlayers");
        Assert.Equal(new[] { "ShowPlayers", "Info" }, history);
    }

    [Fact]
    public void Add_moves_a_repeated_command_to_the_front_without_duplicating()
    {
        var history = RconHistory.Add(new List<string> { "Save", "Info", "ShowPlayers" }, "Info");
        Assert.Equal(new[] { "Info", "Save", "ShowPlayers" }, history);
    }

    [Fact]
    public void Add_trims_whitespace()
    {
        var history = RconHistory.Add(new List<string>(), "  Save  ");
        Assert.Equal(new[] { "Save" }, history);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_ignores_a_blank_command(string blank)
    {
        var history = RconHistory.Add(new List<string> { "Info" }, blank);
        Assert.Equal(new[] { "Info" }, history);
    }

    [Fact]
    public void Add_caps_the_list_at_MaxEntries_dropping_the_oldest()
    {
        var existing = Enumerable.Range(0, RconHistory.MaxEntries).Select(i => $"cmd{i}").ToList();
        var history = RconHistory.Add(existing, "newest");

        Assert.Equal(RconHistory.MaxEntries, history.Count);
        Assert.Equal("newest", history[0]);
        Assert.DoesNotContain($"cmd{RconHistory.MaxEntries - 1}", history); // the oldest fell off the end
    }
}

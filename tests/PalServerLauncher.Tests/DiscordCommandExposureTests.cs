using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class DiscordCommandExposureTests
{
    [Theory]
    [InlineData("status", true)]
    [InlineData("players", true)]
    [InlineData("save", true)]
    [InlineData("backup", true)]
    [InlineData("update", true)]
    [InlineData("announce", true)]
    [InlineData("start", true)]
    [InlineData("unban", true)]
    [InlineData("restart", false)]
    [InlineData("stop", false)]
    [InlineData("kick", false)]
    [InlineData("ban", false)]
    public void Defaults_expose_benign_and_hide_destructive(string command, bool expected)
    {
        Assert.Equal(expected, DiscordBotService.IsCommandEnabled(new LauncherConfig(), command));
    }

    [Fact]
    public void Config_override_wins_over_default()
    {
        var config = new LauncherConfig();
        config.DiscordCommandEnabled["stop"] = true;    // admin opts a destructive one in
        config.DiscordCommandEnabled["status"] = false; // admin opts a benign one out

        Assert.True(DiscordBotService.IsCommandEnabled(config, "stop"));
        Assert.False(DiscordBotService.IsCommandEnabled(config, "status"));
    }

    [Fact]
    public void Unknown_command_is_disabled()
    {
        Assert.False(DiscordBotService.IsCommandEnabled(new LauncherConfig(), "nuke"));
    }

    [Fact]
    public void Every_registered_command_has_a_default_entry()
    {
        // The toggle UI and the resolver both key off AllCommands, so it must list them all.
        Assert.Equal(12, DiscordBotService.AllCommands.Count);
        Assert.All(DiscordBotService.AllCommands, c => Assert.False(string.IsNullOrWhiteSpace(c.Description)));
    }
}

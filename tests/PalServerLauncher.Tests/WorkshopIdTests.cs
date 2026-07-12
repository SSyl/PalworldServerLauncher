using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class WorkshopIdTests
{
    [Theory]
    [InlineData("3625223587", "3625223587")]
    [InlineData("  3625223587  ", "3625223587")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3625223587", "3625223587")]
    [InlineData("https://steamcommunity.com/sharedfiles/filedetails/?id=3625223587&searchtext=x", "3625223587")]
    [InlineData("steamcommunity.com/sharedfiles/filedetails/?id=123", "123")]
    public void Parses_valid_ids(string input, string expected) =>
        Assert.Equal(expected, WorkshopId.TryParse(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notanid")]
    [InlineData("abc123")]
    [InlineData("?id=abc")]
    [InlineData(null)]
    public void Rejects_invalid_input(string? input) =>
        Assert.Null(WorkshopId.TryParse(input));
}

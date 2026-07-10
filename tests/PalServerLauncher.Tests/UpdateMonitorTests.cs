using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class UpdateMonitorTests
{
    [Theory]
    [InlineData("22460594", "22460595", true)]   // newer build published
    [InlineData("22460594", "22460594", false)]  // same build
    [InlineData(null, "22460595", false)]         // not installed - can't compare
    [InlineData("22460594", null, false)]         // couldn't read latest
    [InlineData(null, null, false)]
    [InlineData("", "22460595", false)]           // blank installed
    [InlineData("22460594", "  ", false)]         // blank latest
    public void IsUpdateAvailable_truth_table(string? installed, string? latest, bool expected)
    {
        Assert.Equal(expected, UpdateMonitor.IsUpdateAvailable(installed, latest));
    }

    [Fact]
    public void IsUpdateAvailable_ignores_surrounding_whitespace()
    {
        Assert.False(UpdateMonitor.IsUpdateAvailable(" 22460594 ", "22460594"));
        Assert.True(UpdateMonitor.IsUpdateAvailable(" 22460594 ", "22460595"));
    }
}

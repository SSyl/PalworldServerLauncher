using System.Linq;
using PalServerLauncher.Views;

namespace PalServerLauncher.Tests;

public class RestartTimesDialogTests
{
    [Fact]
    public void EverySchedule_2h_gives_12_evenly_spaced_times()
    {
        var times = RestartTimesDialog.EverySchedule(2);
        Assert.Equal(12, times.Count);
        Assert.Equal(new TimeOnly(0, 0), times[0]);
        Assert.Equal(new TimeOnly(22, 0), times[^1]);
    }

    [Fact]
    public void EverySchedule_5h_steps_from_midnight_and_stops_before_24h()
    {
        Assert.Equal(
            new[] { new TimeOnly(0, 0), new TimeOnly(5, 0), new TimeOnly(10, 0), new TimeOnly(15, 0), new TimeOnly(20, 0) },
            RestartTimesDialog.EverySchedule(5).ToArray());
    }

    [Fact]
    public void EverySchedule_2_5h_leaves_a_shorter_final_gap()
    {
        var times = RestartTimesDialog.EverySchedule(2.5);
        Assert.Equal(10, times.Count);
        Assert.Equal(new TimeOnly(22, 30), times[^1]); // next would be 25:00 -> dropped
    }

    [Fact]
    public void EverySchedule_quarter_hour_gives_96()
    {
        Assert.Equal(96, RestartTimesDialog.EverySchedule(0.25).Count);
    }

    [Theory]
    [InlineData("15m", 0.25)]
    [InlineData("30m", 0.5)]
    [InlineData("45m", 0.75)]
    [InlineData("2h", 2.0)]
    [InlineData("2.5", 2.5)]
    [InlineData("24h", 24.0)]
    public void ParseInterval_reads_presets_and_decimals(string text, double expected) =>
        Assert.Equal(expected, RestartTimesDialog.ParseInterval(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("two")]
    public void ParseInterval_returns_null_for_garbage(string text) =>
        Assert.Null(RestartTimesDialog.ParseInterval(text));
}

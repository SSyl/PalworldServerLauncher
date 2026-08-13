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

    [Fact]
    public void A_build_a_previous_update_failed_to_reach_is_not_offered_again()
    {
        // Issue #15. Without this the attempt fails, the installed build doesn't move, the restart builds a
        // fresh monitor, and it fires again one interval later, forever.
        Assert.False(UpdateMonitor.ShouldOfferUpdate("22460594", "22460595", "22460595"));
    }

    [Fact]
    public void A_newer_build_than_the_one_that_failed_is_offered()
    {
        // Steam moved on, so this is a different update and worth trying.
        Assert.True(UpdateMonitor.ShouldOfferUpdate("22460594", "22460596", "22460595"));
    }

    [Fact]
    public void Nothing_held_behaves_exactly_as_before()
    {
        Assert.True(UpdateMonitor.ShouldOfferUpdate("22460594", "22460595", ""));
        Assert.True(UpdateMonitor.ShouldOfferUpdate("22460594", "22460595", null));
        Assert.False(UpdateMonitor.ShouldOfferUpdate("22460594", "22460594", ""));
    }

    [Fact]
    public void The_hold_does_not_resurrect_an_update_that_is_not_available()
    {
        // A held build that the server is somehow already on must not be offered on the strength of the hold
        // no longer matching.
        Assert.False(UpdateMonitor.ShouldOfferUpdate("22460595", "22460595", "22460595"));
    }

    [Fact]
    public void The_hold_ignores_surrounding_whitespace()
    {
        Assert.False(UpdateMonitor.ShouldOfferUpdate("22460594", " 22460595 ", "22460595"));
    }

    [Fact]
    public void A_remote_build_behind_the_installed_one_is_not_an_update()
    {
        // A stale appinfo cache reports an older build. Acting on it spends a broadcast countdown and a restart
        // to run an app_update that correctly does nothing, then does it again next interval.
        Assert.False(UpdateMonitor.IsUpdateAvailable("24575149", "22460594"));
        Assert.True(UpdateMonitor.IsUpdateAvailable("22460594", "24575149"));
    }

    [Fact]
    public void A_build_id_that_is_not_a_number_still_compares_by_difference()
    {
        // Nothing observed publishes one, so this keeps the original behaviour rather than deciding an ordering
        // for a format we have never seen.
        Assert.True(UpdateMonitor.IsUpdateAvailable("24575149", "public-beta"));
        Assert.False(UpdateMonitor.IsUpdateAvailable("public-beta", "public-beta"));
    }

    [Fact]
    public void Landing_past_the_target_still_counts_as_reaching_it()
    {
        // A target can be minutes old: Check for Update sets one the user may not act on straight away, and
        // Discord's /update sets one for a /restart that comes whenever. Steam publishing a newer build in
        // between makes the update succeed, not fail.
        Assert.True(ServerController.ReachedTarget("24575149", "24575150"));
        Assert.True(ServerController.ReachedTarget("24575149", "24575149"));
        Assert.False(ServerController.ReachedTarget("24575149", "22460594"));
    }

    [Fact]
    public void With_no_target_the_exit_code_is_the_only_verdict()
    {
        Assert.True(ServerController.ReachedTarget(null, "24575149"));
        Assert.True(ServerController.ReachedTarget("", "24575149"));
        Assert.True(ServerController.ReachedTarget("  ", "?"));
    }

    [Fact]
    public void A_target_that_is_not_a_number_must_match_exactly()
    {
        Assert.True(ServerController.ReachedTarget("beta-3", "beta-3"));
        Assert.False(ServerController.ReachedTarget("beta-3", "beta-4"));
    }
}

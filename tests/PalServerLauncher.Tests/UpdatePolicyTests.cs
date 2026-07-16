using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class UpdatePolicyTests
{
    [Theory]
    // pinned, updateOnStart, autoUpdate -> expected (the derived master)
    [InlineData(false, true, false, true)]   // one trigger on -> master on
    [InlineData(false, false, true, true)]   // other trigger on -> master on
    [InlineData(false, false, false, false)] // both off -> master off (does nothing on its own)
    [InlineData(true, true, true, false)]    // pinned -> master off
    public void AnyAutomaticUpdate_is_the_derived_master(bool pinned, bool updateOnStart, bool autoUpdate, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.AnyAutomaticUpdate(pinned, updateOnStart, autoUpdate));

    [Theory]
    // forceUpdate, pinned, updateOnStart -> expected
    [InlineData(false, false, true, true)]    // update-on-start on
    [InlineData(false, false, false, false)]  // update-on-start off
    [InlineData(true, false, false, true)]    // forced update overrides update-on-start being off
    [InlineData(true, true, true, false)]     // pin blocks even a forced update
    [InlineData(false, true, true, false)]    // pin blocks the start-time update
    public void ShouldUpdateBeforeLaunch_pin_blocks_all_force_overrides_start(bool forceUpdate, bool pinned, bool updateOnStart, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.ShouldUpdateBeforeLaunch(forceUpdate, pinned, updateOnStart));

    [Theory]
    // pinned, autoUpdate -> expected
    [InlineData(false, true, true)]    // auto-update on, not pinned
    [InlineData(false, false, false)]  // auto-update off
    [InlineData(true, true, false)]    // pinned overrides
    public void ShouldRunUpdateMonitor_needs_autoupdate_and_no_pin(bool pinned, bool autoUpdate, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.ShouldRunUpdateMonitor(pinned, autoUpdate));

    [Theory]
    [InlineData(false, true)]  // not pinned -> manual check allowed
    [InlineData(true, false)]  // pinned -> manual check off
    public void ManualCheckAllowed_only_blocked_by_pin(bool pinned, bool expected) =>
        Assert.Equal(expected, UpdatePolicy.ManualCheckAllowed(pinned));
}

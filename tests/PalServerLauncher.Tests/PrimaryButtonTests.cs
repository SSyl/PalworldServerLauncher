using PalServerLauncher.State;
using PalServerLauncher.ViewModels;

namespace PalServerLauncher.Tests;

public class PrimaryButtonTests
{
    [Fact]
    public void Not_installed_shows_Install()
    {
        Assert.Equal(PrimaryActionKind.Install, PrimaryButton.Resolve(isInstalled: false, isBusy: false, ServerState.Stopped));
        Assert.Equal("Install", PrimaryButton.Label(false, false, ServerState.Stopped));
        Assert.True(PrimaryButton.CanExecute(false, false, ServerState.Stopped));
    }

    [Fact]
    public void Installed_and_stopped_shows_Start()
    {
        Assert.Equal(PrimaryActionKind.Start, PrimaryButton.Resolve(true, false, ServerState.Stopped));
        Assert.Equal("▶", PrimaryButton.Label(true, false, ServerState.Stopped));
    }

    [Theory]
    [InlineData(ServerState.Healthy)]
    [InlineData(ServerState.Degraded)]
    [InlineData(ServerState.Zombie)]
    public void Running_shows_Stop(ServerState state)
    {
        Assert.Equal(PrimaryActionKind.Stop, PrimaryButton.Resolve(true, false, state));
        Assert.Equal("■", PrimaryButton.Label(true, false, state));
    }

    [Theory]
    [InlineData(ServerState.Starting, "Starting…")]
    [InlineData(ServerState.Stopping, "Stopping…")]
    [InlineData(ServerState.Restarting, "Stopping…")]
    public void Transitional_states_are_disabled(ServerState state, string expectedLabel)
    {
        Assert.Equal(PrimaryActionKind.None, PrimaryButton.Resolve(true, false, state));
        Assert.False(PrimaryButton.CanExecute(true, false, state));
        Assert.Equal(expectedLabel, PrimaryButton.Label(true, false, state));
    }

    [Fact]
    public void Busy_disables_and_overrides_everything()
    {
        Assert.Equal(PrimaryActionKind.None, PrimaryButton.Resolve(isInstalled: false, isBusy: true, ServerState.Stopped));
        Assert.False(PrimaryButton.CanExecute(true, true, ServerState.Healthy));
        Assert.Equal("Working…", PrimaryButton.Label(true, true, ServerState.Stopped));
    }

    [Fact]
    public void Backoff_allows_Start()
    {
        // Auto-restart suspended, but the user may still manually start.
        Assert.Equal(PrimaryActionKind.Start, PrimaryButton.Resolve(true, false, ServerState.Backoff));
    }

    [Fact]
    public void Timed_shutdown_countdown_shows_a_clickable_ShutdownNow()
    {
        // While a timed shutdown counts down the state is still Stopping, but the button becomes an amber,
        // clickable "shut down now" that accelerates the countdown.
        Assert.Equal(PrimaryActionKind.ShutdownNow, PrimaryButton.Resolve(true, false, ServerState.Stopping, timedShutdownRemaining: 58));
        Assert.Equal("Stopping (58s)", PrimaryButton.Label(true, false, ServerState.Stopping, timedShutdownRemaining: 58));
        Assert.True(PrimaryButton.CanExecute(true, false, ServerState.Stopping, timedShutdownRemaining: 58));
    }

    [Fact]
    public void No_countdown_keeps_Stopping_disabled()
    {
        // A null remaining preserves the normal transitional behavior.
        Assert.Equal(PrimaryActionKind.None, PrimaryButton.Resolve(true, false, ServerState.Stopping, timedShutdownRemaining: null));
        Assert.False(PrimaryButton.CanExecute(true, false, ServerState.Stopping));
        Assert.Equal("Stopping…", PrimaryButton.Label(true, false, ServerState.Stopping));
    }

    [Fact]
    public void Busy_still_overrides_a_timed_shutdown()
    {
        Assert.Equal(PrimaryActionKind.None, PrimaryButton.Resolve(true, isBusy: true, ServerState.Stopping, timedShutdownRemaining: 58));
    }
}

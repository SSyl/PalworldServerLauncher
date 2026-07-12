using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The relaunch-suppression state machine that backs "a deliberate stop stays stopped". These encode the
/// exact race outcomes the lifecycle fixes guarantee (see "Fix lifecycle race conditions in the stop path").
/// </summary>
public class RelaunchGateTests
{
    [Fact]
    public void Fresh_gate_allows_a_launch()
    {
        var gate = new RelaunchGate();
        Assert.True(gate.MayLaunch(alreadyRunning: false));
        Assert.False(gate.Suppressed);
    }

    [Fact]
    public void Already_running_never_launches()
    {
        var gate = new RelaunchGate();
        Assert.False(gate.MayLaunch(alreadyRunning: true)); // the never-double-launch guard
    }

    [Fact]
    public void Deliberate_stop_suppresses_the_next_relaunch()
    {
        // H2: a Stop that races zombie recovery must leave the server down.
        var gate = new RelaunchGate();
        gate.SuppressForDeliberateStop();
        Assert.True(gate.Suppressed);
        Assert.False(gate.MayLaunch(alreadyRunning: false));
    }

    [Fact]
    public void A_restarts_own_start_does_not_clear_a_deliberate_stop()
    {
        // H3: a Stop during an in-progress restart must not be undone by the restart's relaunch.
        var gate = new RelaunchGate();
        gate.SuppressForDeliberateStop();
        gate.OnStart(userInitiated: false); // the restart's internal start
        Assert.False(gate.MayLaunch(alreadyRunning: false));
    }

    [Fact]
    public void A_user_start_clears_the_suppression()
    {
        var gate = new RelaunchGate();
        gate.SuppressForDeliberateStop();
        gate.OnStart(userInitiated: true); // an explicit user Start
        Assert.False(gate.Suppressed);
        Assert.True(gate.MayLaunch(alreadyRunning: false));
    }

    [Fact]
    public void A_user_start_on_a_fresh_gate_stays_launchable()
    {
        var gate = new RelaunchGate();
        gate.OnStart(userInitiated: true);
        Assert.True(gate.MayLaunch(alreadyRunning: false));
    }

    [Fact]
    public void Suppression_survives_a_restart_start_then_clears_on_the_user_start()
    {
        // Full sequence: user stops mid-restart, the restart tries to relaunch (blocked), then the user Starts.
        var gate = new RelaunchGate();
        gate.SuppressForDeliberateStop();
        gate.OnStart(userInitiated: false);
        Assert.False(gate.MayLaunch(alreadyRunning: false));
        gate.OnStart(userInitiated: true);
        Assert.True(gate.MayLaunch(alreadyRunning: false));
    }
}

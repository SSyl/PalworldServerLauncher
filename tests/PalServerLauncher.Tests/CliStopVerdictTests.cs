using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The rules a CLI stop is resolved by, before anything is done. These encode which combinations a script sees as
/// success, which are refused, and which latch the relaunch gate. The latch cases are the load-bearing ones: latch
/// too little and a restart's own relaunch undoes the stop, latch too much and a still-running server silently
/// loses crash auto-restart.
/// </summary>
public class CliStopVerdictTests
{
    private static ServerController.CliStopVerdict Resolve(
        StopKind kind = StopKind.Graceful, bool adopted = true, bool anyServerRunning = true, bool restUsable = true) =>
        ServerController.ResolveCliStop(kind, adopted, anyServerRunning, restUsable);

    [Fact]
    public void An_adopted_server_with_rest_proceeds()
    {
        Assert.Equal(ServerController.CliStopVerdict.Proceed, Resolve());
        Assert.Equal(ServerController.CliStopVerdict.Proceed, Resolve(StopKind.Countdown));
        Assert.Equal(ServerController.CliStopVerdict.Proceed, Resolve(StopKind.Kill));
    }

    [Fact]
    public void Nothing_running_at_all_is_nothing_to_stop()
    {
        Assert.Equal(ServerController.CliStopVerdict.NothingToStop, Resolve(adopted: false, anyServerRunning: false));
    }

    [Fact]
    public void A_running_but_unadopted_server_is_refused_not_reported_as_stopped()
    {
        // The listener is up from the controller's constructor while adoption can still be sitting behind a
        // modal startup prompt. Claiming success there would tell a script the server is down while it is up.
        Assert.Equal(ServerController.CliStopVerdict.NotAdopted, Resolve(adopted: false, anyServerRunning: true));
    }

    [Theory]
    [InlineData(StopKind.Graceful)]
    [InlineData(StopKind.Countdown)]
    public void A_stop_that_cannot_save_is_refused(StopKind kind)
    {
        Assert.Equal(ServerController.CliStopVerdict.NeedsRestApi, Resolve(kind, restUsable: false));
    }

    [Fact]
    public void Kill_does_not_need_rest()
    {
        // Force is its own flag, and it is the documented answer to a REST-off server.
        Assert.Equal(ServerController.CliStopVerdict.Proceed, Resolve(StopKind.Kill, restUsable: false));
    }

    [Fact]
    public void Only_verdicts_that_mean_the_server_should_be_down_latch_the_gate()
    {
        Assert.True(ServerController.ShouldLatchRelaunchGate(ServerController.CliStopVerdict.Proceed));

        // The restart window: a restart between its own stop and start has no process bound for minutes, and
        // without a latch here its relaunch would undo the stop the script just asked for.
        Assert.True(ServerController.ShouldLatchRelaunchGate(ServerController.CliStopVerdict.NothingToStop));
    }

    [Theory]
    [InlineData(ServerController.CliStopVerdict.NeedsRestApi)]
    [InlineData(ServerController.CliStopVerdict.NotAdopted)]
    public void A_refusal_never_latches_the_gate(ServerController.CliStopVerdict verdict)
    {
        // The server is still running and we did not stop it. Latching would suppress crash auto-restart and
        // zombie recovery until someone pressed Start, with nothing on screen to say why.
        Assert.False(ServerController.ShouldLatchRelaunchGate(verdict));
    }
}

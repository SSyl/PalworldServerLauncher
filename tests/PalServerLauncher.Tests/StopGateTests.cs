using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The one-stop-at-a-time state machine. The cases that matter are the two ends: a stop still running must be
/// joined (two ladders both POST /shutdown and the later one silently rewrites the countdown players were given),
/// and a finished stop must never block the next one.
/// </summary>
public class StopGateTests
{
    [Fact]
    public void A_fresh_gate_has_nothing_to_join()
    {
        Assert.Null(new StopGate().InFlight);
    }

    [Fact]
    public void A_running_stop_is_handed_back_to_join()
    {
        var gate = new StopGate();
        var running = new TaskCompletionSource().Task;

        gate.Claim(running);

        Assert.Same(running, gate.InFlight);
    }

    [Fact]
    public void A_finished_stop_does_not_block_the_next_one()
    {
        // No explicit release anywhere, so this is what keeps the gate from latching shut forever.
        var gate = new StopGate();
        var finished = new TaskCompletionSource();
        gate.Claim(finished.Task);

        finished.SetResult();

        Assert.Null(gate.InFlight);
    }

    [Fact]
    public void A_faulted_stop_also_frees_the_gate()
    {
        var gate = new StopGate();
        var faulted = new TaskCompletionSource();
        gate.Claim(faulted.Task);

        faulted.SetException(new InvalidOperationException("ladder blew up"));

        Assert.Null(gate.InFlight);
        Assert.True(faulted.Task.IsFaulted);
    }

    [Fact]
    public void A_cancelled_stop_also_frees_the_gate()
    {
        var gate = new StopGate();
        var cancelled = new TaskCompletionSource();
        gate.Claim(cancelled.Task);

        cancelled.SetCanceled();

        Assert.Null(gate.InFlight);
    }

    [Fact]
    public void Claiming_again_after_one_finishes_tracks_the_new_stop()
    {
        var gate = new StopGate();
        var first = new TaskCompletionSource();
        gate.Claim(first.Task);
        first.SetResult();

        var second = new TaskCompletionSource().Task;
        gate.Claim(second);

        Assert.Same(second, gate.InFlight);
    }
}

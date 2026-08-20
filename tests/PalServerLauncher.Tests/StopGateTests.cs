using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The one-stop-at-a-time gate. The property everything depends on is that the slot frees on EVERY exit path, a
/// ladder that throws or is cancelled included, because nothing ever releases it explicitly. A latched gate would
/// mean no stop ever runs again.
/// </summary>
public class StopGateTests
{
    [Fact]
    public async Task The_first_caller_runs_the_ladder()
    {
        var gate = new StopGate();
        var ran = false;

        await gate.RunOrJoin(() => { ran = true; return Task.CompletedTask; });

        Assert.True(ran);
    }

    [Fact]
    public async Task A_second_caller_joins_instead_of_running_a_second_ladder()
    {
        var gate = new StopGate();
        var release = new TaskCompletionSource();
        var runs = 0;

        var first = gate.RunOrJoin(() => { runs++; return release.Task; });
        var second = gate.RunOrJoin(() => { runs++; return Task.CompletedTask; });

        Assert.Equal(1, runs); // the second ladder must never have been started
        Assert.False(second.IsCompleted);

        release.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_joiner_completes_when_the_running_ladder_does_not_before()
    {
        var gate = new StopGate();
        var release = new TaskCompletionSource();
        var first = gate.RunOrJoin(() => release.Task);
        var second = gate.RunOrJoin(() => Task.CompletedTask);

        Assert.False(second.IsCompleted);
        release.SetResult();

        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await first;
    }

    [Fact]
    public async Task A_finished_stop_frees_the_gate_for_the_next_one()
    {
        var gate = new StopGate();
        await gate.RunOrJoin(() => Task.CompletedTask);

        var ranAgain = false;
        await gate.RunOrJoin(() => { ranAgain = true; return Task.CompletedTask; });

        Assert.True(ranAgain);
    }

    [Fact]
    public async Task A_throwing_ladder_frees_the_gate_and_faults_only_its_own_caller()
    {
        var gate = new StopGate();

        var boom = gate.RunOrJoin(() => Task.FromException(new InvalidOperationException("ladder blew up")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => boom);

        Assert.False(gate.IsRunning);
        var ranAgain = false;
        await gate.RunOrJoin(() => { ranAgain = true; return Task.CompletedTask; });
        Assert.True(ranAgain);
    }

    [Fact]
    public async Task A_joiner_does_not_see_the_running_ladders_exception()
    {
        // One failed ladder must not resurface as an unobserved exception once per joiner.
        var gate = new StopGate();
        var release = new TaskCompletionSource();

        var first = gate.RunOrJoin(() => release.Task);
        var joiner = gate.RunOrJoin(() => Task.CompletedTask);

        release.SetException(new InvalidOperationException("ladder blew up"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await joiner.WaitAsync(TimeSpan.FromSeconds(5)); // completes, does not throw
    }

    [Fact]
    public async Task A_cancelled_ladder_frees_the_gate()
    {
        var gate = new StopGate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var cancelled = gate.RunOrJoin(() => Task.FromCanceled(cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        Assert.False(gate.IsRunning);
        var ranAgain = false;
        await gate.RunOrJoin(() => { ranAgain = true; return Task.CompletedTask; });
        Assert.True(ranAgain);
    }

    [Fact]
    public async Task A_ladder_that_throws_synchronously_still_frees_the_gate()
    {
        // Thrown before the returned task exists at all, so the release has to survive that too.
        var gate = new StopGate();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gate.RunOrJoin(() => throw new InvalidOperationException("threw before awaiting")));

        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task IsRunning_tracks_the_ladder()
    {
        var gate = new StopGate();
        Assert.False(gate.IsRunning);

        var release = new TaskCompletionSource();
        var running = gate.RunOrJoin(() => release.Task);
        Assert.True(gate.IsRunning);

        // Awaited, not just released: the slot frees when the gate's own completion propagates, one scheduling
        // hop after the ladder's task finishes, so IsRunning briefly over-reports. Conservative on purpose.
        release.SetResult();
        await running;

        Assert.False(gate.IsRunning);
    }

    [Fact]
    public async Task Concurrent_callers_still_only_run_one_ladder()
    {
        var gate = new StopGate();
        var release = new TaskCompletionSource();
        var runs = 0;
        var start = new TaskCompletionSource();
        var allPastTheGate = new TaskCompletionSource();
        var stillToArrive = 16;

        var callers = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            var ladder = gate.RunOrJoin(() => { Interlocked.Increment(ref runs); return release.Task; });
            // After RunOrJoin returns, so every caller is through the gate's lock before the release
            // below. A fixed delay let a loaded runner release first, and stragglers then ran own ladders.
            if (Interlocked.Decrement(ref stillToArrive) == 0)
                allPastTheGate.TrySetResult();
            await ladder;
        })).ToArray();

        start.SetResult();
        await allPastTheGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        await Task.WhenAll(callers).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, runs);
    }
}

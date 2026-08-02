using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// Serializes the stop ladder: the first caller runs it, anyone arriving while it is still running joins that same
/// completion. Two ladders at once both save and back up (colliding on the same second-resolution backup filename)
/// and both POST /shutdown, where the LATER call wins, so a stop landing inside a timed shutdown would silently
/// rewrite the countdown players were already shown.
///
/// Self-contained and thread-safe on purpose: it owns the claim, the run, and the release together, so the one
/// property everything depends on (the slot is freed on every exit path, including a throwing or cancelled ladder)
/// is testable here rather than only reachable through <see cref="ServerController"/>.
/// </summary>
public sealed class StopGate
{
    private readonly object _sync = new();
    private Task? _inFlight;

    /// <summary>Whether a stop is running right now, for a request that must not queue behind one. Frees a
    /// scheduling hop after the ladder's own task finishes, so it can briefly over-report a stop that has just
    /// ended. That direction is the safe one: it refuses a request rather than letting two ladders overlap.</summary>
    public bool IsRunning
    {
        get { lock (_sync) return _inFlight is { IsCompleted: false }; }
    }

    /// <summary>
    /// Run <paramref name="startLadder"/>, or return the ladder already running so the caller joins it instead.
    /// The ladder is started OUTSIDE the lock: it sets <see cref="ServerController"/>'s State, whose handlers are
    /// documented as unsafe to raise while the controller's lock is held, and this lock is taken on that same path.
    /// </summary>
    public Task RunOrJoin(Func<Task> startLadder)
    {
        TaskCompletionSource signal;
        lock (_sync)
        {
            if (_inFlight is { IsCompleted: false })
                return _inFlight;

            // Claim before releasing the lock, so a caller arriving mid-start joins rather than racing.
            signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = signal.Task;
        }

        return RunAsync(signal, startLadder);
    }

    private static async Task RunAsync(TaskCompletionSource signal, Func<Task> startLadder)
    {
        try
        {
            await startLadder().ConfigureAwait(false);
        }
        finally
        {
            // Joiners only learn that the stop finished and check the server state themselves. Completing rather
            // than faulting the signal keeps one failed ladder from resurfacing as an unobserved exception per
            // joiner, while the caller that actually started it still gets the real exception from its own task.
            signal.TrySetResult();
        }
    }
}

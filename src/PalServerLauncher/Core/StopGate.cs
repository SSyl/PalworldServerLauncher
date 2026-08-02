using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// One stop ladder at a time. Two at once both write a shutdown backup onto the same second-resolution filename,
/// and both POST /shutdown, where Palworld honors the LATER call, so a stop landing inside a timed shutdown
/// silently rewrites the countdown players were already shown.
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
            // Completed rather than faulted: the starter still gets the exception from its own task, and faulting
            // this one would resurface the same failure as an unobserved exception once per joiner.
            signal.TrySetResult();
        }
    }
}

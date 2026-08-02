using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// Serializes the stop ladder, extracted from <see cref="ServerController"/> so its small state machine is
/// unit-testable (same reasoning as <see cref="RelaunchGate"/>). Two ladders running at once both save and back up
/// (colliding on the same second-resolution backup filename) and both POST /shutdown, where the LATER call wins:
/// a "shut down now" landing inside a timed shutdown silently rewrites the countdown players were already given.
/// A second caller joins the running stop instead of starting its own.
///
/// Not thread-safe on its own: the controller holds its lock around every call, so this stays a plain field-flip.
/// </summary>
public sealed class StopGate
{
    private Task? _inFlight;

    /// <summary>The stop still running, or null when the caller should start one. A finished stop never blocks the
    /// next one, so the gate needs no explicit release.</summary>
    public Task? InFlight => _inFlight is { IsCompleted: false } ? _inFlight : null;

    /// <summary>Record the stop about to start. Claimed BEFORE the ladder runs, so a caller arriving mid-ladder
    /// joins rather than racing it.</summary>
    public void Claim(Task stop) => _inFlight = stop;
}

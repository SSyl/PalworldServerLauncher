namespace PalServerLauncher.Core;

/// <summary>
/// The relaunch-suppression latch, extracted from <see cref="ServerController"/> so its small state machine is
/// unit-testable. A deliberate stop (plain Stop, timed shutdown, or Force Shutdown) suppresses any auto-recovery
/// or restart relaunch until the next user Start. Not thread-safe on its own: the controller holds its lock
/// around every call, so this stays a plain field-flip (no locking, no allocation on the hot path).
/// </summary>
public sealed class RelaunchGate
{
    private bool _suppressed;

    /// <summary>True while a deliberate stop is latched, so a pending relaunch will be dropped.</summary>
    public bool Suppressed => _suppressed;

    /// <summary>A deliberate stop happened. Suppress the next relaunch until a user Start clears it.</summary>
    public void SuppressForDeliberateStop() => _suppressed = true;

    /// <summary>Whether a launch may proceed: only when nothing is already running and no deliberate stop is latched.</summary>
    public bool MayLaunch(bool alreadyRunning) => !alreadyRunning && !_suppressed;

    /// <summary>A user Start (<paramref name="userInitiated"/> true) clears the suppression. A restart's own start
    /// passes false, so a stop the user asked for mid-restart keeps the server down.</summary>
    public void OnStart(bool userInitiated)
    {
        if (userInitiated)
            _suppressed = false;
    }
}

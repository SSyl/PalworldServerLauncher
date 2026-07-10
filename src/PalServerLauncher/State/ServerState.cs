namespace PalServerLauncher.State;

/// <summary>
/// Lifecycle state of the managed Palworld server, as tracked by the launcher.
/// Crash detection (process exit) and health probing (REST /metrics) both feed transitions here.
/// </summary>
public enum ServerState
{
    /// <summary>No matching server process is running.</summary>
    Stopped,

    /// <summary>Process is up but hasn't reported a healthy REST response yet (booting).</summary>
    Starting,

    /// <summary>Process up, REST responding, simulation advancing (uptime increasing, fps &gt; 0).</summary>
    Healthy,

    /// <summary>Process up but a health probe failed (REST unreachable, or uptime/fps frozen). Not yet past the failure threshold.</summary>
    Degraded,

    /// <summary>Process up but confirmed wedged past the failure threshold. Recovery (kill + relaunch) is warranted.</summary>
    Zombie,

    /// <summary>A plain stop is in progress (save + shutdown), with no relaunch to follow.</summary>
    Stopping,

    /// <summary>A controlled restart is in progress (save + shutdown + relaunch, possibly applying an update).</summary>
    Restarting,

    /// <summary>Auto-recovery is suspended after too many restarts in a short window (likely bad update or corrupt save).</summary>
    Backoff
}

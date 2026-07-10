namespace PalServerLauncher.Core;

/// <summary>
/// Why a restart is happening - drives the in-game broadcast wording so players know whether the
/// server is going down for an update, a scheduled maintenance window, or an admin's manual restart.
/// </summary>
public enum RestartReason
{
    Update,
    Scheduled,
    Manual,
}

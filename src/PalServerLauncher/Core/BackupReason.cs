namespace PalServerLauncher.Core;

/// <summary>Why a backup is being taken - drives the archive filename suffix and the log wording.</summary>
public enum BackupReason
{
    Startup,
    Shutdown,
    Scheduled,
    Manual,
}

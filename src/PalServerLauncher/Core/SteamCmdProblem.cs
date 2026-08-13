namespace PalServerLauncher.Core;

/// <summary>What went wrong with a SteamCMD run, where we can say. Nothing here is inferred from a cause table:
/// the disk answers are probed, DamagedFiles comes from state bits, and everything else stays Unknown and lets
/// SteamCMD's own content-log lines speak.</summary>
public enum SteamCmdProblem
{
    /// <summary>Nothing we can add. Offer the redownload, since it's the only lever we have.</summary>
    Unknown,
    /// <summary>The drive is short of the room the download needs, measured rather than reported.</summary>
    DiskSpace,
    /// <summary>The server folder refused a write, measured rather than reported.</summary>
    DiskWrite,
    /// <summary>Files Missing or Files Corrupt. The one case a redownload answers rather than guesses at.</summary>
    DamagedFiles,
}

/// <summary>
/// What the launcher asked SteamCMD to do, which is what a failure may claim went wrong. app_update is the same
/// command in every case, so this cannot be read back off SteamCMD, and getting it wrong tells a user an update
/// failed when none was pending.
/// </summary>
public enum SteamCmdOperation
{
    /// <summary>A first install, from the Install button.</summary>
    Install,
    /// <summary>A file check of an existing install, from the Validate Files button.</summary>
    Validate,
    /// <summary>Applying a build already known to be newer, from the update prompt.</summary>
    Update,
    /// <summary>Making sure the server is current, which usually has nothing to apply. The pre-launch check.</summary>
    VerifyOrUpdate,
}

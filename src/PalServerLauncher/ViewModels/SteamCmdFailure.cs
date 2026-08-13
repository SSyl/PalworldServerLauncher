using System.Collections.Generic;
using PalServerLauncher.Core;

namespace PalServerLauncher.ViewModels;

/// <summary>A failed SteamCMD run, as the View needs to describe it. <paramref name="State"/> and
/// <paramref name="Reasons"/> are SteamCMD's own words, shown verbatim: they are what a search matches on and
/// the only account of the failure the user gets.</summary>
/// <param name="Operation">What SteamCMD was doing, which decides the wording and the offered actions.</param>
/// <param name="ExitCode">SteamCMD's process exit code.</param>
/// <param name="Problem">What went wrong, as far as we can tell. Only complete once WithCurrentDiskState has
/// run, which happens as the offer is made.</param>
/// <param name="State">SteamCMD's reported state, e.g. <c>0x6</c>, or null when it printed none.</param>
/// <param name="ChangedFiles">Whether the run had started changing the install, from the state bits, or null
/// when there was no state to read.</param>
/// <param name="ReachedApp">Whether SteamCMD got as far as evaluating the server. False means it stopped before
/// that, so the failure is not an update problem and nothing here can repair it.</param>
/// <param name="LaunchPending">Whether a server launch is waiting on this, which is the only case where starting
/// on the installed build is an answer. Independent of <paramref name="Operation"/>.</param>
/// <param name="Reasons">The explanatory lines from SteamCMD's content log, oldest first, possibly empty.</param>
/// <param name="RepairSpaceGb">Free space a full repair could need. Null until WithCurrentDiskState fills it,
/// and after that when the install size couldn't be read.</param>
/// <param name="FreeSpace">Free space on the server's drive, formatted, on the same terms as RepairSpaceGb.</param>
public sealed record SteamCmdFailure(
    SteamCmdOperation Operation,
    int ExitCode,
    SteamCmdProblem Problem,
    string? State,
    bool? ChangedFiles,
    bool ReachedApp,
    bool LaunchPending,
    IReadOnlyList<string> Reasons,
    double? RepairSpaceGb,
    string? FreeSpace)
{
    /// <summary>Set once the repair has been tried and failed, so it is not offered a second time.</summary>
    public bool RepairFailed { get; init; }

    /// <summary>
    /// Whether the repair can plausibly help. False when SteamCMD never reached the server, on a full or
    /// unwritable disk, and on a first install, where there is nothing yet to re-resolve.
    ///
    /// Still offered after a failed Validate Files. That runs the same validate but keeps the app manifest,
    /// and the repair sets the manifest aside first, which is what forces SteamCMD to resolve the app again
    /// rather than trust it. Measured: validate against a manifest holding a wrong build exits 0 and does
    /// nothing at all.
    ///
    /// Low disk space no longer withholds it. The repair validates, so it usually fetches nothing, and denying
    /// the one cheap fix over a download that probably won't happen is worse than trying it and failing, which
    /// puts the manifest back anyway. An unwritable folder is different: nothing can be repaired into it.
    /// </summary>
    public bool OfferRepair =>
        !RepairFailed &&
        ReachedApp &&
        Operation != SteamCmdOperation.Install &&
        Problem != SteamCmdProblem.DiskWrite;

    /// <summary>Whether the user gets to launch on the build already installed.</summary>
    public bool OfferStartAnyway => LaunchPending;
}

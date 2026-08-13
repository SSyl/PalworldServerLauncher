namespace PalServerLauncher.ViewModels;

/// <summary>What to do after a SteamCMD run failed. Which of these are actually offered depends on the run,
/// see <see cref="SteamCmdFailure.OfferRedownload"/> and <see cref="SteamCmdFailure.OfferStartAnyway"/>.</summary>
public enum SteamCmdFailureChoice
{
    /// <summary>Leave the install as it is. Before a launch that starts the build already there, which is the
    /// usual answer. From the Install or Update button it just ends the attempt.</summary>
    Leave,
    /// <summary>Check the installed files against Steam's and run the update again, fetching whatever fails
    /// the check.</summary>
    ValidateAndRetry,
    /// <summary>Before a launch only: don't start the server at all.</summary>
    Cancel,
}

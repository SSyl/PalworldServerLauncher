using System;
using System.Collections.Generic;
using System.Linq;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;

namespace PalServerLauncher.ViewModels;

/// <summary>Turns a <see cref="SteamCmdFailure"/> into the dialog's text and button labels. Pure and static so
/// the wording is decided somewhere a test can read it.</summary>
public static class SteamCmdFailureText
{
    /// <summary>The paragraph above the detail box.</summary>
    public static string Message(SteamCmdFailure failure)
    {
        var codes = failure.State is { } state
            ? string.Format(Strings.SteamCmd_CodesBoth, failure.ExitCode, state)
            : string.Format(Strings.SteamCmd_CodesExit, failure.ExitCode);

        // Claim only what the launcher actually asked for. SteamCMD can fail before reaching the server at all
        // (Steam unreachable, no network, its own install broken), and the pre-launch check usually has no
        // update to apply, so "couldn't update the server" is wrong in both cases.
        var headline = !failure.ReachedApp ? Strings.SteamCmd_StoppedEarly : failure.Operation switch
        {
            SteamCmdOperation.Install => Strings.SteamCmd_CouldntInstall,
            SteamCmdOperation.Validate => Strings.SteamCmd_CouldntVerify,
            SteamCmdOperation.Update => Strings.SteamCmd_CouldntUpdate,
            _ => Strings.SteamCmd_CouldntVerifyOrUpdate,
        };

        var lines = new List<string> { string.Format(headline, codes) };

        if (failure.RepairFailed)
            lines.Add(Strings.SteamCmd_ValidateFailed);
        if (Explanation(failure) is { } explanation)
            lines.Add(explanation);
        else if (failure.ChangedFiles == false)
            lines.Add(Strings.SteamCmd_Untouched);

        lines.Add("");
        lines.Add(failure.Reasons.Count > 0 ? Strings.SteamCmd_Reported : Strings.SteamCmd_NoReason);
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>The question under the detail box, or null when there is nothing to offer. Below rather than
    /// above, so it reads as following from SteamCMD's own account rather than interrupting it.</summary>
    public static string? Footer(SteamCmdFailure failure) =>
        failure.OfferRepair ? Strings.SteamCmd_ValidateOffer : null;

    /// <summary>SteamCMD's own lines for the detail box, or null when it gave none and there is no box.</summary>
    public static string? Detail(SteamCmdFailure failure) =>
        failure.Reasons.Count == 0 ? null : string.Join(Environment.NewLine, failure.Reasons);

    /// <summary>The buttons left to right, paired with what each answers. The last is what a dismissed dialog
    /// falls back to.</summary>
    public static IReadOnlyList<(string Label, SteamCmdFailureChoice Choice)> Buttons(SteamCmdFailure failure)
    {
        var buttons = new List<(string, SteamCmdFailureChoice)>();

        if (failure.OfferRepair)
            buttons.Add((Strings.SteamCmd_ValidateRetry, SteamCmdFailureChoice.ValidateAndRetry));

        // Cancel last, so dismissing the dialog with Escape or the title bar declines rather than launches.
        if (failure.OfferStartAnyway)
        {
            buttons.Add((Strings.SteamCmd_StartAnyway, SteamCmdFailureChoice.Leave));
            buttons.Add((Strings.Common_Cancel, SteamCmdFailureChoice.Cancel));
        }
        else
            buttons.Add((Strings.SteamCmd_Close, SteamCmdFailureChoice.Leave));

        return buttons;
    }

    /// <summary>What we can state rather than guess, already decided by the controller.</summary>
    private static string? Explanation(SteamCmdFailure failure) => failure.Problem switch
    {
        // Reports the space rather than asserting the disk caused the failure.
        SteamCmdProblem.DiskSpace => string.Format(Strings.SteamCmd_DiskSpace, failure.FreeSpace, failure.RepairSpaceGb ?? 0),
        SteamCmdProblem.DiskWrite => Strings.SteamCmd_DiskWrite,
        SteamCmdProblem.DamagedFiles => Strings.SteamCmd_Damaged,
        _ => null,
    };
}

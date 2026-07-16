namespace PalServerLauncher.ViewModels;

/// <summary>How the user chose to handle a detected WorldOption.sav on Start, from the pre-launch prompt
/// (the dialog lives in the View). WorldOption.sav overrides PalWorldSettings.ini on a dedicated server.</summary>
public enum WorldOptionChoice
{
    /// <summary>User cancelled the prompt, don't start the server.</summary>
    Cancel,
    /// <summary>Rename WorldOption.sav to .bak so the server reads PalWorldSettings.ini, then start.</summary>
    RenameToBak,
    /// <summary>Leave the file in place and start anyway (not recommended).</summary>
    ContinueAnyway,
}

/// <summary>Outcome of a RenameToBak choice: the .bak paths that were renamed (for the View to show as
/// reveal-in-Explorer links), or a failure carrying its error message.</summary>
public readonly record struct WorldOptionRenameResult(bool Success, IReadOnlyList<string> BakPaths, string? Error);

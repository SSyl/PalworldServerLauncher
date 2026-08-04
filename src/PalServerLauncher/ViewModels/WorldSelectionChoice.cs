namespace PalServerLauncher.ViewModels;

/// <summary>How the user chose to handle a save the server isn't pointed at, from the pre-launch prompt (the
/// dialog lives in the View). Starting a fresh world is a real choice here, not just a fallback: clearing
/// DedicatedServerName is also how a user deliberately begins a new world next to an old one.</summary>
public enum WorldSelectionChoice
{
    /// <summary>User cancelled the prompt, don't start the server.</summary>
    Cancel,
    /// <summary>Point GameUserSettings.ini at the save on disk, then start.</summary>
    LoadExistingWorld,
    /// <summary>Leave the config alone and let the server generate a new empty world.</summary>
    StartNewWorld,
}

namespace PalServerLauncher.Core;

/// <summary>
/// Pure decision logic for when the launcher may update the server. A version pin is the hard override:
/// nothing updates while pinned and the manual check is off. Otherwise two independent triggers apply,
/// "update on start" (a SteamCMD app_update before launch) and "auto-update while running" (the background
/// build-id monitor). An explicit forced update (an update-restart) overrides "update on start" being off but
/// never the pin. Kept pure and static so the settings UI and the controller apply the same rules.
/// </summary>
public static class UpdatePolicy
{
    /// <summary>Whether any automatic update trigger is active. This is the derived "Automatic updates" master
    /// state, it is on only when an actual trigger is on, so it can never sit "on" doing nothing.</summary>
    public static bool AnyAutomaticUpdate(bool versionPinned, bool updateOnStart, bool autoUpdateEnabled) =>
        !versionPinned && (updateOnStart || autoUpdateEnabled);

    /// <summary>Whether Start/restart should run a SteamCMD app_update before launching. A pin blocks it entirely.
    /// Otherwise an explicit forced update (an update-restart) runs regardless, and a normal Start updates only
    /// when Update-on-start is set.</summary>
    public static bool ShouldUpdateBeforeLaunch(bool forceUpdate, bool versionPinned, bool updateOnStart) =>
        !versionPinned && (forceUpdate || updateOnStart);

    /// <summary>Whether the background build-id monitor should run while the server is up.</summary>
    public static bool ShouldRunUpdateMonitor(bool versionPinned, bool autoUpdateEnabled) =>
        !versionPinned && autoUpdateEnabled;

    /// <summary>Whether the manual "Check for Update" action is available. Only a pin disables it.</summary>
    public static bool ManualCheckAllowed(bool versionPinned) => !versionPinned;
}

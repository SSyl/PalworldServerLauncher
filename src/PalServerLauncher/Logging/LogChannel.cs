namespace PalServerLauncher.Logging;

/// <summary>
/// Which log stream a line belongs to. The All tab shows every line, and the other five tabs each show one
/// channel. Note <see cref="General"/> means the launcher's own lines and feeds the tab labeled Launcher,
/// so the enum member and the All tab are not the same thing despite how the names read.
/// </summary>
public enum LogChannel
{
    General,
    SteamCmd,
    Server,
    Chat,
    PlayerJoin,
}

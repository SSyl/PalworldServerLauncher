namespace PalServerLauncher.Logging;

/// <summary>
/// Which log stream a line belongs to. The General tab shows every line; the SteamCmd, Server, Chat, and
/// PlayerJoin tabs each show only their own channel.
/// </summary>
public enum LogChannel
{
    General,
    SteamCmd,
    Server,
    Chat,
    PlayerJoin,
}

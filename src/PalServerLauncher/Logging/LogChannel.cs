namespace PalServerLauncher.Logging;

/// <summary>
/// Which log stream a line belongs to. The General tab shows every line; the SteamCmd and Server
/// tabs show only their own channel (tailed from SteamCMD's console_log.txt and the server's Pal.log).
/// </summary>
public enum LogChannel
{
    General,
    SteamCmd,
    Server,
}

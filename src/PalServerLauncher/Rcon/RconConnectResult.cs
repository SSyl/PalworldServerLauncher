namespace PalServerLauncher.Rcon;

/// <summary>Outcome of an RCON connect + authenticate attempt, so the console can show a specific reason.</summary>
public enum RconConnectResult
{
    /// <summary>Connected and authenticated. Commands can be sent.</summary>
    Connected,

    /// <summary>Reached the port but the password was rejected (wrong or empty AdminPassword).</summary>
    AuthFailed,

    /// <summary>Couldn't open the TCP connection (server down, RCON disabled, wrong port, or timed out).</summary>
    Unreachable,

    /// <summary>An unexpected error (malformed response, socket fault mid-handshake).</summary>
    Error,
}

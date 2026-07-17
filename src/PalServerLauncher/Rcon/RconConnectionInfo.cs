namespace PalServerLauncher.Rcon;

/// <summary>The local server's RCON connection details, read from PalWorldSettings.ini when the Server Commands
/// dialog opens. <see cref="Enabled"/> gates whether the RCON tab is offered at all (hidden when RCON is off in
/// the ini). Host is always loopback: the launcher only manages its own local server.</summary>
public sealed record RconConnectionInfo(bool Enabled, string Host, int Port, string Password);

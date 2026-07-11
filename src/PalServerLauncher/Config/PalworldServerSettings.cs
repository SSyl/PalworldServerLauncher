namespace PalServerLauncher.Config;

/// <summary>
/// The handful of values the launcher reads (never writes) out of PalWorldSettings.ini.
/// All are nullable so callers can distinguish "absent" from "set to a default-looking value".
/// </summary>
public sealed record PalworldServerSettings
{
    public int? RestApiPort { get; init; }
    public bool? RestApiEnabled { get; init; }
    public string? AdminPassword { get; init; }
    public int? PublicPort { get; init; }
    public int? RconPort { get; init; }
    public bool? RconEnabled { get; init; }

    /// <summary>REST API port, falling back to Palworld's default (8212) when unset.</summary>
    public int RestApiPortOrDefault => RestApiPort ?? 8212;

    /// <summary>Public game port, falling back to Palworld's default (8211) when unset.</summary>
    public int PublicPortOrDefault => PublicPort ?? 8211;

    /// <summary>RCON port, falling back to Palworld's default (25575) when unset.</summary>
    public int RconPortOrDefault => RconPort ?? 25575;

    /// <summary>
    /// The REST API is only reachable when it is explicitly enabled AND a non-empty AdminPassword
    /// is set, the game engine silently disables the API when the password is blank
    /// (PalworldServer-main/README.md:35). This drives the onboarding gate.
    /// </summary>
    public bool RestApiUsable => (RestApiEnabled ?? false) && !string.IsNullOrEmpty(AdminPassword);
}

using System.Text.Json.Serialization;

namespace PalServerLauncher.Rest.Models;

/// <summary>Response of <c>GET /v1/api/players</c>.</summary>
public sealed class PlayersResponse
{
    [JsonPropertyName("players")] public List<Player> Players { get; init; } = new();
}

public sealed class Player
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("accountName")] public string? AccountName { get; init; }
    [JsonPropertyName("playerId")] public string? PlayerId { get; init; }
    [JsonPropertyName("userId")] public string? UserId { get; init; }
    [JsonPropertyName("ip")] public string? Ip { get; init; }
    [JsonPropertyName("ping")] public double Ping { get; init; }
    [JsonPropertyName("location_x")] public double LocationX { get; init; }
    [JsonPropertyName("location_y")] public double LocationY { get; init; }
    [JsonPropertyName("level")] public int Level { get; init; }
    [JsonPropertyName("building_count")] public int BuildingCount { get; init; }
}

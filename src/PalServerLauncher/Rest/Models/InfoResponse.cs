using System.Text.Json.Serialization;

namespace PalServerLauncher.Rest.Models;

/// <summary>Response of <c>GET /v1/api/info</c>. A successful response is our "server is ready" gate.</summary>
public sealed class InfoResponse
{
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("servername")] public string? ServerName { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("worldguid")] public string? WorldGuid { get; init; }
}

using System.Text.Json.Serialization;

namespace PalServerLauncher.Rest.Models;

/// <summary>
/// Response of <c>GET /v1/api/metrics</c>. This is the primary health signal:
/// <see cref="Uptime"/> must advance and <see cref="ServerFps"/> must be &gt; 0 on a live server.
/// Field names match the API exactly (all lowercase). Unknown fields are ignored on deserialize.
/// </summary>
public sealed class MetricsResponse
{
    [JsonPropertyName("serverfps")] public int ServerFps { get; init; }
    [JsonPropertyName("currentplayernum")] public int CurrentPlayerNum { get; init; }
    [JsonPropertyName("serverframetime")] public double ServerFrameTime { get; init; }
    [JsonPropertyName("maxplayernum")] public int MaxPlayerNum { get; init; }
    [JsonPropertyName("uptime")] public long Uptime { get; init; }
    [JsonPropertyName("days")] public int Days { get; init; }
    [JsonPropertyName("basecampnum")] public int BaseCampNum { get; init; }
}

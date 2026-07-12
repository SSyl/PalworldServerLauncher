using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Rest;

/// <summary>A mod's public Steam Workshop metadata, from the keyless GetPublishedFileDetails API.</summary>
public sealed record SteamModDetails(string Id, string Title, int ConsumerAppId, long TimeUpdated);

/// <summary>
/// Fetches a Workshop item's public details (title, owning app, last-updated) from Steam's keyless
/// <c>ISteamRemoteStorage/GetPublishedFileDetails/v1/</c> endpoint, so the launcher can auto-fill a mod
/// name without downloading it. The JSON parse is a pure static so it's unit-tested; the HTTP call returns
/// null on any failure (offline, bad shape) so the caller treats it as "name unavailable".
/// </summary>
public sealed class SteamWorkshopClient : IDisposable
{
    /// <summary>Palworld's Steam app id, where Workshop content lives (matches SteamCmd.GameAppId).</summary>
    public const int PalworldAppId = 1623730;

    private static readonly Uri Endpoint =
        new("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public SteamWorkshopClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _ownsHttp = http is null;
    }

    public async Task<SteamModDetails?> GetDetailsAsync(string workshopId, CancellationToken ct = default)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["itemcount"] = "1",
                ["publishedfileids[0]"] = workshopId,
            });
            using var response = await _http.PostAsync(Endpoint, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return ParseDetails(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Parse the GetPublishedFileDetails JSON. Null if the item wasn't found (per-item result != 1)
    /// or the shape is unexpected.</summary>
    public static SteamModDetails? ParseDetails(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp)
                || !resp.TryGetProperty("publishedfiledetails", out var arr)
                || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
                return null;

            var d = arr[0];
            // Per-item result: 1 = ok. Anything else (deleted / private / not found) -> no usable details.
            if (d.TryGetProperty("result", out var res) && res.TryGetInt32(out var r) && r != 1)
                return null;

            var id = GetString(d, "publishedfileid") ?? "";
            var title = GetString(d, "title") ?? "";
            var appId = d.TryGetProperty("consumer_app_id", out var a) && a.TryGetInt32(out var ai) ? ai : 0;
            var updated = d.TryGetProperty("time_updated", out var t) && t.TryGetInt64(out var tu) ? tu : 0L;
            return new SteamModDetails(id, title, appId, updated);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

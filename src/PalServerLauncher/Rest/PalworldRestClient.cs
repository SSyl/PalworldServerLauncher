using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Rest.Models;

namespace PalServerLauncher.Rest;

/// <summary>
/// Thin wrapper over the Palworld REST API (<c>http://host:port/v1/api/</c>, HTTP Basic auth
/// <c>admin:&lt;AdminPassword&gt;</c>). GET helpers return <c>null</c> on any failure (unreachable,
/// non-2xx, or bad JSON) so the health monitor can treat null as "probe failed"; command POSTs
/// return a success bool. Unknown JSON fields are ignored, keeping us forward-compatible with 1.0.
/// </summary>
public sealed class PalworldRestClient : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;

    public PalworldRestClient(int restApiPort, string adminPassword, string host = "127.0.0.1", TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri($"http://{host}:{restApiPort}/v1/api/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
        };

        var token = Convert.ToBase64String(Encoding.ASCII.GetBytes($"admin:{adminPassword}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // --- Reads (null == probe failed) ---
    public Task<InfoResponse?> GetInfoAsync(CancellationToken ct = default) => GetAsync<InfoResponse>("info", ct);
    public Task<MetricsResponse?> GetMetricsAsync(CancellationToken ct = default) => GetAsync<MetricsResponse>("metrics", ct);
    public Task<PlayersResponse?> GetPlayersAsync(CancellationToken ct = default) => GetAsync<PlayersResponse>("players", ct);

    // --- Commands (bool == accepted) ---
    public Task<bool> AnnounceAsync(string message, CancellationToken ct = default) =>
        PostAsync("announce", new { message }, ct);

    public Task<bool> SaveAsync(CancellationToken ct = default) => PostAsync("save", body: null, ct);

    /// <summary>Graceful shutdown: the server renders a <paramref name="waittimeSeconds"/> countdown in-game.</summary>
    public Task<bool> ShutdownAsync(int waittimeSeconds, string message, CancellationToken ct = default) =>
        PostAsync("shutdown", new { waittime = waittimeSeconds, message }, ct);

    public Task<bool> StopAsync(CancellationToken ct = default) => PostAsync("stop", body: null, ct);

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ObjectDisposedException)
        {
            // ObjectDisposedException: the client was rebuilt/disposed mid-probe - treat as a failed read.
            return null;
        }
    }

    private async Task<bool> PostAsync(string path, object? body, CancellationToken ct)
    {
        try
        {
            using HttpContent? content = body is null
                ? null
                : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync(path, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose() => _http.Dispose();
}

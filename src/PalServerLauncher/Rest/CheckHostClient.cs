using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Rest;

/// <summary>
/// Thin wrapper over the check-host.cc probe API (<c>https://api.check-host.cc/</c>). Submits a check
/// (<c>POST /{method}</c> where method is "udp" or "tcp") of a public IP:port from a set of external
/// nodes, then polls its report (<c>GET /report/{uuid}</c>). Used by <see cref="Core.PortChecker"/>
/// to verify a port is reachable from the internet. Returns null / false on any failure so callers
/// treat it as "service unavailable" rather than "port closed". The report parsing lives in the pure,
/// unit-tested <see cref="CheckHostReport"/>.
/// </summary>
public sealed class CheckHostClient : IDisposable
{
    private static readonly string[] DefaultRegions = { "US" };

    private readonly HttpClient _http;

    public CheckHostClient(TimeSpan? timeout = null)
    {
        // A generous timeout: check-host's submit/poll round-trips are slower than the local REST API.
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.check-host.cc/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        };
        // check-host content-negotiates; without this some endpoints answer with HTML instead of JSON.
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>True if the check-host API answers at all, the "Port Check Service Online" precheck.</summary>
    public async Task<bool> IsServiceUpAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync("", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Submit a probe (<paramref name="method"/> is "udp" or "tcp") of <paramref name="target"/>:
    /// <paramref name="port"/>. Returns the task uuid to poll, or null on failure. An optional
    /// <paramref name="payload"/> is sent as the UDP datagram body (hex-encoded) so our echo listener
    /// has non-empty bytes to bounce back.
    /// </summary>
    public async Task<string?> SubmitAsync(string method, string target, int port, byte[]? payload = null,
        IReadOnlyList<string>? regions = null, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object>
        {
            ["target"] = target,
            ["port"] = port,
            ["region"] = regions ?? DefaultRegions,
            ["repeatchecks"] = 0,
        };
        if (payload is { Length: > 0 })
            body["payload"] = "0x" + Convert.ToHexString(payload).ToLowerInvariant();

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(method, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return doc.RootElement.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String
                ? uuid.GetString()
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Fetch and parse the current report for a submitted uuid. Null on an HTTP failure (retry);
    /// an empty list means the report exists but no node has reported yet.</summary>
    public async Task<IReadOnlyList<CheckHostNode>?> PollAsync(string uuid, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"report/{uuid}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return CheckHostReport.ParseNodes(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

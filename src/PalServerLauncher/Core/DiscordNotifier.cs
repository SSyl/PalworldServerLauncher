using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// Posts lifecycle notifications to a Discord webhook (REST). No-op unless enabled with a URL set.
/// Fire-and-forget and best-effort, a webhook failure never affects the server. Wired to the
/// controller's lifecycle in <see cref="ServerController"/>; extend by calling <see cref="Notify"/>
/// from more sites and gating on <see cref="LauncherConfig.DiscordNotifyLifecycle"/> /
/// <see cref="LauncherConfig.DiscordNotifyPlayers"/>.
/// </summary>
public sealed class DiscordNotifier : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public DiscordNotifier(LauncherConfig config, Logger logger)
    {
        _config = config;
        _logger = logger;
    }

    /// <summary>Fire-and-forget a webhook message (does nothing when Discord is off / no URL).</summary>
    public void Notify(string message)
    {
        if (!_config.DiscordEnabled || string.IsNullOrWhiteSpace(_config.DiscordWebhookUrl))
            return;
        _ = PostAsync(message);
    }

    private async Task PostAsync(string message)
    {
        try
        {
            // allowed_mentions.parse = [] so an untrusted name (e.g. a player called "@everyone") in the
            // message can never actually ping anyone.
            var payload = JsonSerializer.Serialize(new { content = message, allowed_mentions = new { parse = Array.Empty<string>() } });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync(_config.DiscordWebhookUrl, content).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger.Debug($"Discord webhook returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            // Includes a malformed webhook URL (InvalidOperationException / UriFormatException), so a bad URL
            // logs and is dropped rather than faulting this fire-and-forget task.
            _logger.Debug($"Discord webhook failed: {ex.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}

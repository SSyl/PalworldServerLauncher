using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// Detects the machine's public IPv4 address from a lightweight external service, used for the External
/// IP display and as the target of the port checks. Returns null on any failure so the caller can fall
/// back to manual entry. Tries a couple of plain-text "what's my IP" endpoints in turn. This is one of the
/// launcher's outbound calls (disclosed in the README Privacy section).
/// </summary>
public static class PublicIpLookup
{
    private static readonly string[] Endpoints =
    {
        "https://api.ipify.org",
        "https://ifconfig.me/ip",
        "https://icanhazip.com",
    };

    private static readonly Regex Ipv4 = new(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.Compiled);

    public static async Task<string?> DetectPublicIpAsync(CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        foreach (var endpoint in Endpoints)
        {
            try
            {
                var text = (await http.GetStringAsync(endpoint, ct).ConfigureAwait(false)).Trim();
                if (Ipv4.IsMatch(text))
                    return text;
            }
            catch (HttpRequestException)
            {
                // Try the next endpoint.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // This endpoint timed out (not a user cancel), try the next.
            }
        }
        return null;
    }
}

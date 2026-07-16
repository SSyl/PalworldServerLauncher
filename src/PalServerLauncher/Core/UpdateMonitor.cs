using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// While one server instance is running, polls SteamCMD's published build id against the installed
/// one and raises <see cref="UpdateFound"/> when they diverge. One monitor per running process,
/// created/disposed by the controller, so it never touches SteamCMD while the server is stopped.
/// The build-id query is read-only (<c>app_info_print</c>, hidden); the controller applies the actual
/// update (broadcast -> stop -> app_update -> start) in response to <see cref="UpdateFound"/>.
///
/// One-shot: after signalling once it stops querying, so it can't re-trigger during the broadcast
/// countdown; the restart disposes it and a fresh monitor starts with the new build.
/// </summary>
public sealed class UpdateMonitor : IDisposable
{
    private readonly LauncherConfig _config;
    private readonly Func<CancellationToken, Task<string?>> _queryLatestBuildId;
    private readonly Func<string?> _readInstalledBuildId;
    private readonly Func<string?, string> _buildDisplay;
    private readonly Logger _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _fired;
    private bool _disposed;

    public event Action? UpdateFound;
    public event Action<string>? StatusChanged;

    public UpdateMonitor(
        LauncherConfig config,
        Func<CancellationToken, Task<string?>> queryLatestBuildId,
        Func<string?> readInstalledBuildId,
        Func<string?, string> buildDisplay,
        Logger logger)
    {
        _config = config;
        _queryLatestBuildId = queryLatestBuildId;
        _readInstalledBuildId = readInstalledBuildId;
        _buildDisplay = buildDisplay;
        _logger = logger;
    }

    /// <summary>Whether a build id difference means an update is available (ignores nulls / whitespace).</summary>
    public static bool IsUpdateAvailable(string? installed, string? latest) =>
        !string.IsNullOrWhiteSpace(installed) && !string.IsNullOrWhiteSpace(latest) &&
        !string.Equals(installed.Trim(), latest.Trim(), StringComparison.Ordinal);

    public void Start() => _ = LoopAsync(_cts.Token);

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_config.UpdateCheckInterval);
        try
        {
            // Wait one interval before the first check, Start already ran app_update, so an immediate
            // query would just confirm "up to date" and waste a SteamCMD launch.
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await CheckAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Monitor stopped.
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (_fired)
            return;

        var installed = _readInstalledBuildId();
        if (string.IsNullOrWhiteSpace(installed))
            return; // not installed (shouldn't happen while running) - nothing to compare

        // Respect live config changes: while the pin/master/auto-update policy says no, don't poll SteamCMD.
        // The monitor isn't created when these are already off at launch, this covers flipping one mid-run
        // (e.g. pinning a running server), which stops the polling without a restart.
        if (!UpdatePolicy.ShouldRunUpdateMonitor(_config.VersionPinEnabled, _config.AutoUpdateEnabled))
        {
            StatusChanged?.Invoke(_config.VersionPinEnabled
                ? string.Format(Strings.Update_Pinned, _buildDisplay(_config.PinnedBuildId.Length > 0 ? _config.PinnedBuildId : installed))
                : string.Format(Strings.Update_AutoUpdateOff, _buildDisplay(installed)));
            return;
        }

        StatusChanged?.Invoke(Strings.Update_Checking);
        var latest = await _queryLatestBuildId(ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(latest))
        {
            _logger.Debug("Update check: could not read latest build id (will retry next interval).");
            StatusChanged?.Invoke(string.Format(Strings.Update_CheckFailed, _buildDisplay(installed)));
            return;
        }

        if (IsUpdateAvailable(installed, latest))
        {
            _fired = true;
            _logger.Info($"New server build {latest} found (installed {installed}), starting update restart.");
            StatusChanged?.Invoke(string.Format(Strings.Update_Found, latest));
            UpdateFound?.Invoke();
        }
        else
        {
            _logger.Debug($"Update check: up to date (build {installed}).");
            StatusChanged?.Invoke(string.Format(Strings.Update_UpToDate, _buildDisplay(installed)));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}

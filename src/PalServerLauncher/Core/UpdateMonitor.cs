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
    private bool _heldAnnounced;
    private bool _disposed;

    /// <summary>Carries the build being offered, so the update that follows can tell whether it got there.</summary>
    public event Action<string>? UpdateFound;
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

    /// <summary>
    /// Whether a build id difference means an update is available (ignores nulls / whitespace). Steam build ids
    /// climb, so a remote id BEHIND the installed one is not an update, and acting on it spends a broadcast
    /// countdown and a restart on an app_update that correctly does nothing. LinuxGSM documents SteamCMD
    /// reporting stale info from its appinfo cache, which is how that arises. Ids that don't parse fall back to
    /// inequality.
    /// </summary>
    public static bool IsUpdateAvailable(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
            return false;

        installed = installed.Trim();
        latest = latest.Trim();
        return long.TryParse(installed, out var have) && long.TryParse(latest, out var published)
            ? published > have
            : !string.Equals(installed, latest, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether to offer <paramref name="latest"/>, given a build a previous attempt failed to reach. Retrying
    /// the same build on a timer is issue #15's loop: the attempt fails, the installed build doesn't move, the
    /// restart builds a fresh monitor, and it fires again an interval later.
    ///
    /// Keys on whether the installed build reached the target, which makes it independent of why the attempt
    /// failed. That matters because SteamCMD can exit zero having applied nothing, and a stale appinfo.vdf can
    /// report a build that was never published, neither of which reports an error anywhere.
    /// </summary>
    public static bool ShouldOfferUpdate(string? installed, string? latest, string? failedBuildId) =>
        IsUpdateAvailable(installed, latest) &&
        !string.Equals(latest?.Trim(), failedBuildId?.Trim(), StringComparison.Ordinal);

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

        if (ShouldOfferUpdate(installed, latest, _config.FailedUpdateBuildId))
        {
            _fired = true;
            _logger.Info($"New server build {latest} found (installed {installed}), starting update restart.");
            StatusChanged?.Invoke(string.Format(Strings.Update_Found, latest));
            UpdateFound?.Invoke(latest.Trim());
        }
        else if (IsUpdateAvailable(installed, latest))
        {
            // Only the log line is suppressed, not the polling: latching _fired here would disable update
            // checking for the rest of the server's uptime, missing any build published after this one.
            if (!_heldAnnounced)
            {
                _heldAnnounced = true;
                _logger.Info($"Build {latest} available, installed {installed}. The last update to it failed, " +
                             $"so it will not be retried automatically. Use Check for Update to retry.");
            }
            StatusChanged?.Invoke(string.Format(Strings.Update_Failed, _buildDisplay(latest)));
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

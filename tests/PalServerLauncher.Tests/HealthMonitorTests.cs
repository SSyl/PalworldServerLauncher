using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The pure metrics-probe decision. Two behaviours are load-bearing: (1) a probe cancelled by the monitor
/// being disposed must never be mistaken for a real failure, or it would fire a spurious Degraded / Zombie
/// (and drive a recovery relaunch) after the monitor is gone; (2) REST enabled but never answering past the
/// boot grace (and never having been healthy) must resolve to Unreachable, so the UI stops hanging on
/// Starting, WITHOUT escalating to a failure/recovery (the game server itself is fine).
/// </summary>
public class HealthMonitorTests
{
    [Fact]
    public void Cancelled_probe_with_no_metrics_is_ignored_not_a_failure()
    {
        // The exact race: disposal cancels the read, the REST client returns null, and without the guard this
        // reads as "unreachable" and registers a failure after the monitor is already gone.
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: true, hasMetrics: false, reachedHealthy: true, frozen: false, bootGraceElapsed: false));
    }

    [Fact]
    public void Cancelled_probe_with_metrics_is_ignored()
    {
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: true, hasMetrics: true, reachedHealthy: true, frozen: false, bootGraceElapsed: false));
    }

    [Fact]
    public void Cancelled_beats_the_boot_grace()
    {
        // Even past the grace with no metrics, a cancelled (disposed) probe fires nothing.
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: true, hasMetrics: false, reachedHealthy: false, frozen: false, bootGraceElapsed: true));
    }

    [Fact]
    public void Unreachable_after_healthy_is_a_failure()
    {
        // Once we'd been healthy, losing REST is a real failure regardless of how long we've been up (grace
        // is only about the initial boot), so it must not be downgraded to the non-recovering Unreachable.
        Assert.Equal(ProbeAction.Failure,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: false, reachedHealthy: true, frozen: false, bootGraceElapsed: true));
    }

    [Fact]
    public void No_metrics_within_the_boot_grace_is_ignored()
    {
        // Still booting: no REST response yet is expected, not a failure and not yet "unreachable".
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: false, reachedHealthy: false, frozen: false, bootGraceElapsed: false));
    }

    [Fact]
    public void No_metrics_past_the_boot_grace_is_unreachable()
    {
        // REST was expected (a client exists) but never answered past the grace and we never went healthy:
        // surface RestUnreachable instead of hanging on Starting. Not a failure -> no recovery.
        Assert.Equal(ProbeAction.Unreachable,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: false, reachedHealthy: false, frozen: false, bootGraceElapsed: true));
    }

    [Fact]
    public void Live_metrics_are_healthy()
    {
        Assert.Equal(ProbeAction.Healthy,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: true, reachedHealthy: true, frozen: false, bootGraceElapsed: false));
    }

    [Fact]
    public void First_metrics_sample_promotes_to_healthy()
    {
        // hasMetrics with reachedHealthy still false is the Starting -> Healthy promotion, grace is irrelevant.
        Assert.Equal(ProbeAction.Healthy,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: true, reachedHealthy: false, frozen: false, bootGraceElapsed: true));
    }

    [Fact]
    public void Frozen_metrics_are_a_failure()
    {
        Assert.Equal(ProbeAction.Failure,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: true, reachedHealthy: true, frozen: true, bootGraceElapsed: false));
    }

    [Theory]
    [InlineData(false, false)] // still booting, within the grace
    [InlineData(false, true)]  // still booting, past the grace
    [InlineData(true, false)]  // cancelled mid-boot
    [InlineData(true, true)]
    public void A_server_that_never_went_healthy_is_never_escalated_to_a_failure(bool cancelled, bool bootGraceElapsed)
    {
        // Safety invariant behind the RestUnreachable fix: a server that has never responded (never healthy)
        // with no metrics must never resolve to Failure, the only action that drives Degraded/Zombie recovery.
        // So a slow-booting or REST-misconfigured server is surfaced (Ignore/Unreachable) but never killed and
        // relaunched. Guards against a future change that folds the Unreachable case back into Failure.
        var action = HealthMonitor.EvaluateMetricsProbe(
            cancelled: cancelled, hasMetrics: false, reachedHealthy: false, frozen: false, bootGraceElapsed: bootGraceElapsed);
        Assert.NotEqual(ProbeAction.Failure, action);
    }
}

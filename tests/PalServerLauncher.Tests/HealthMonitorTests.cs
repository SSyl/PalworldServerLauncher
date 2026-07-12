using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The pure metrics-probe decision behind the disposal-race fix: a probe cancelled by the monitor being
/// disposed must never be mistaken for a real failure, or it would fire a spurious Degraded / Zombie (and
/// drive a recovery relaunch) after the monitor is gone.
/// </summary>
public class HealthMonitorTests
{
    [Fact]
    public void Cancelled_probe_with_no_metrics_is_ignored_not_a_failure()
    {
        // The exact race: disposal cancels the read, the REST client returns null, and without the guard this
        // reads as "unreachable" and registers a failure after the monitor is already gone.
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: true, hasMetrics: false, reachedHealthy: true, frozen: false));
    }

    [Fact]
    public void Cancelled_probe_with_metrics_is_ignored()
    {
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: true, hasMetrics: true, reachedHealthy: true, frozen: false));
    }

    [Fact]
    public void Unreachable_after_healthy_is_a_failure()
    {
        Assert.Equal(ProbeAction.Failure,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: false, reachedHealthy: true, frozen: false));
    }

    [Fact]
    public void Unreachable_before_the_first_healthy_sample_is_ignored()
    {
        // Still booting: no REST response yet is expected, not a failure.
        Assert.Equal(ProbeAction.Ignore,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: false, reachedHealthy: false, frozen: false));
    }

    [Fact]
    public void Live_metrics_are_healthy()
    {
        Assert.Equal(ProbeAction.Healthy,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: true, reachedHealthy: true, frozen: false));
    }

    [Fact]
    public void Frozen_metrics_are_a_failure()
    {
        Assert.Equal(ProbeAction.Failure,
            HealthMonitor.EvaluateMetricsProbe(cancelled: false, hasMetrics: true, reachedHealthy: true, frozen: true));
    }
}

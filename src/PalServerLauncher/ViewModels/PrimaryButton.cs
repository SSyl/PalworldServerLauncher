using PalServerLauncher.State;

namespace PalServerLauncher.ViewModels;

/// <summary>What clicking the multi-state primary button should do, given the current state.</summary>
public enum PrimaryActionKind
{
    /// <summary>No action, transitional/busy; the button is disabled.</summary>
    None,
    Install,
    Start,
    Stop,
    /// <summary>A timed shutdown is counting down; the button is an amber "shut down now" (accelerate) affordance.</summary>
    ShutdownNow,
}

/// <summary>
/// Pure state -> (label, action, enabled) mapping for the single multi-state primary button
/// (the Conan-launcher pattern). Kept free of WPF/controller dependencies so it is unit-testable.
/// The button reads "Install" until the server exists, then "Start"/"Stop", and shows a disabled
/// transitional label while installing/starting/stopping.
/// </summary>
public static class PrimaryButton
{
    public static PrimaryActionKind Resolve(bool isInstalled, bool isBusy, ServerState state, int? timedShutdownRemaining = null)
    {
        if (isBusy) return PrimaryActionKind.None;
        if (!isInstalled) return PrimaryActionKind.Install;
        // During a timed shutdown the server is mid-Stopping, but we surface an amber, clickable "shut down now"
        // (accelerate the countdown) instead of the usual disabled transitional label.
        if (timedShutdownRemaining is not null) return PrimaryActionKind.ShutdownNow;

        return state switch
        {
            ServerState.Stopped or ServerState.Backoff => PrimaryActionKind.Start,
            ServerState.Starting or ServerState.Stopping or ServerState.Restarting => PrimaryActionKind.None,
            _ => PrimaryActionKind.Stop, // Healthy / Degraded / Zombie == running
        };
    }

    public static string Label(bool isInstalled, bool isBusy, ServerState state, int? timedShutdownRemaining = null)
    {
        if (isBusy) return "Working…";
        if (!isInstalled) return "Install";
        if (timedShutdownRemaining is int seconds) return $"Stopping ({seconds}s)";

        return state switch
        {
            ServerState.Stopped or ServerState.Backoff => "▶",  // Start (play)
            ServerState.Starting => "Starting…",
            ServerState.Stopping or ServerState.Restarting => "Stopping…",
            _ => "■",  // Stop (running)
        };
    }

    public static bool CanExecute(bool isInstalled, bool isBusy, ServerState state, int? timedShutdownRemaining = null) =>
        Resolve(isInstalled, isBusy, state, timedShutdownRemaining) != PrimaryActionKind.None;
}

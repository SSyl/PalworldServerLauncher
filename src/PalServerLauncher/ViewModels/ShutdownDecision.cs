namespace PalServerLauncher.ViewModels;

/// <summary>How the user chose to stop the server, from the Stop-button prompt (the dialog lives in the View).</summary>
public enum ShutdownKind
{
    /// <summary>User cancelled the prompt, do nothing.</summary>
    Cancel,
    /// <summary>REST is off, so a direct kill (force stop) is the only option.</summary>
    ForceNoRest,
    /// <summary>Immediate graceful shutdown (save + shutdown, no countdown).</summary>
    GracefulNow,
    /// <summary>Graceful shutdown after an in-game countdown of <see cref="ShutdownDecision.Seconds"/>.</summary>
    Timed,
}

/// <summary>The resolved Stop-button choice: what kind of shutdown, and the countdown length for <see cref="ShutdownKind.Timed"/>.</summary>
public readonly record struct ShutdownDecision(ShutdownKind Kind, int Seconds = 0);

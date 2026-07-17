namespace PalServerLauncher.ViewModels;

/// <summary>How the user chose to handle a running Palworld server this launcher didn't start, from the
/// pre-Start prompt (the dialog lives in the View). Such a server can conflict on ports or become a duplicate.</summary>
public enum UnknownServerChoice
{
    /// <summary>User cancelled the prompt, don't start the server.</summary>
    Cancel,
    /// <summary>Terminate the unmanaged server process(es), then start.</summary>
    Terminate,
    /// <summary>Leave the other server running and start anyway.</summary>
    LeaveRunning,
}

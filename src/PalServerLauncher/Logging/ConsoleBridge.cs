using System.IO;
using System.Runtime.InteropServices;

namespace PalServerLauncher.Logging;

/// <summary>
/// Gives this GUI-subsystem (WPF) process a console so log output can be watched from a terminal.
/// Attaches to the launching terminal when there is one (ATTACH_PARENT_PROCESS); otherwise allocates
/// a fresh console window. Deliberately GUI-independent so a future headless mode can reuse it.
/// </summary>
public static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    /// <summary>
    /// Attach (or allocate) a console and point <see cref="Console.Out"/> at it. Returns true if a
    /// console is now available for writing.
    /// </summary>
    public static bool Enable()
    {
        var attached = AttachConsole(AttachParentProcess) || AllocConsole();
        if (!attached)
            return false;

        // After attach/alloc the CLR's cached stdout handle is stale - repoint it at the real console.
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
        return true;
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PalServerLauncher;

/// <summary>
/// Paints a window's OS title bar with the Windows dark theme. WPF leaves the title bar light even when the
/// content is dark, so we set the DWM immersive-dark-mode attribute (with the pre-20H1 attribute id as a
/// fallback on older Windows 10). A no-op on OSes that don't support it, the call just fails and the bar stays light.
/// </summary>
public static class DarkTitleBar
{
    private const int UseImmersiveDarkMode = 20;           // Windows 10 20H1 (build 19041) and later, Windows 11
    private const int UseImmersiveDarkModeBefore20H1 = 19;  // Windows 10 1809-1909

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Apply the dark title bar to a window. Call once its handle exists (SourceInitialized or Loaded).</summary>
    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var enabled = 1;
        if (DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
    }
}

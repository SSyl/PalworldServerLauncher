using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace PalServerLauncher.Core;

/// <summary>
/// Manages a per-user Startup-folder shortcut that opens the launcher with <c>--start-server</c> at login, so
/// the launcher starts (or adopts) the server and manages it: scheduled restarts, backups, health recovery,
/// update checks. User-scoped, no elevation, and unique-named per install so several copies don't collide.
/// </summary>
public static class LoginShortcut
{
    private static string Hash(string exePath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToLowerInvariant())))[..8];

    /// <summary>This install's Startup shortcut path (unique per exe location so several copies don't collide).</summary>
    public static string ShortcutPath(string exePath) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"Palworld Server Launcher ({Hash(exePath)}).lnk");

    /// <summary>True when this install's login shortcut is present.</summary>
    public static bool Exists(string exePath) => File.Exists(ShortcutPath(exePath));

    /// <summary>Create the login shortcut targeting the exe with <c>--start-server</c>. No elevation.</summary>
    public static bool Create(string exePath)
    {
        var lnk = ShortcutPath(exePath);
        var workingDir = Path.GetDirectoryName(Path.GetFullPath(exePath)) ?? "";
        var script =
            "$shell = New-Object -ComObject WScript.Shell\n" +
            $"$lnk = $shell.CreateShortcut({PsLiteral(lnk)})\n" +
            $"$lnk.TargetPath = {PsLiteral(exePath)}\n" +
            "$lnk.Arguments = '--start-server'\n" +
            $"$lnk.WorkingDirectory = {PsLiteral(workingDir)}\n" +
            "$lnk.Save()\n";
        return RunPowerShell(script) && File.Exists(lnk);
    }

    /// <summary>Remove the login shortcut (a direct file delete, no elevation).</summary>
    public static bool Remove(string exePath)
    {
        try
        {
            var lnk = ShortcutPath(exePath);
            if (File.Exists(lnk))
                File.Delete(lnk);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

    private static bool RunPowerShell(string script)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process is null)
                return false;
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

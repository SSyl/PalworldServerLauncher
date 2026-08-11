using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

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

    private const string StartupApprovedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    /// <summary>
    /// True when Windows has this install's shortcut switched off in Settings > Startup apps (the same switch as
    /// the Task Manager Startup tab). The shortcut file stays on disk when that happens, so <see cref="Exists"/>
    /// keeps reporting true while nothing launches at login. False whenever the key or value can't be read.
    /// </summary>
    public static bool IsDisabledByWindows(string exePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKey);
            return IsDisabledValue(key?.GetValue(Path.GetFileName(ShortcutPath(exePath))) as byte[]);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Decode a StartupApproved value. Windows sets bit 0 of the first byte to disable an entry (02 -> 03
    /// for Startup-folder items) and appends a FILETIME of when it happened. Undocumented but stable since
    /// Windows 8. A missing value means enabled, since Windows only writes one once the switch is used.</summary>
    public static bool IsDisabledValue(byte[]? value) => value is { Length: > 0 } && (value[0] & 1) != 0;

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

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>What a cross-platform import actually found in the source folder.</summary>
public readonly record struct ImportedPayload(bool Saves, IReadOnlyList<string> ConfigFiles);

/// <summary>What kind of Palworld dedicated server install a folder holds.</summary>
public enum ServerInstallKind
{
    /// <summary>Not a server install (or unreadable).</summary>
    None,

    /// <summary>A Windows install: it has the console server exe, so it can be copied in and run as-is.</summary>
    Windows,

    /// <summary>A Linux install. Its binaries can't run here, so only the world and settings are worth taking.</summary>
    Linux,
}

/// <summary>
/// Copies an existing (non-launcher) Palworld dedicated server install into the launcher's managed folder,
/// leaving the original untouched. The source is verified to look like a real install first. Version-agnostic:
/// it copies whatever files are there, so it works for any game version.
/// </summary>
public static class ServerImporter
{
    /// <summary>The world and the settings, relative to a server root. Everything worth carrying out of an install
    /// whose binaries we can't reuse. Palworld names the config folder after the platform, hence the two.</summary>
    public const string SaveGamesRelative = @"Pal\Saved\SaveGames";
    public const string LinuxConfigRelative = @"Pal\Saved\Config\LinuxServer";
    public const string WindowsConfigRelative = @"Pal\Saved\Config\WindowsServer";

    /// <summary>Config files a cross-platform import carries: the server settings, and GameUserSettings.ini for
    /// its DedicatedServerName, without which the new install would start an empty world beside the copied save.</summary>
    public static readonly string[] PortableConfigFiles = ["PalWorldSettings.ini", GameUserSettingsFile.FileName];

    /// <summary>True when <paramref name="dir"/> looks like a Palworld dedicated server install, i.e. it holds
    /// the console server exe at its usual sub-path. Name-independent, so the folder can be called anything.</summary>
    public static bool LooksLikeServerInstall(string dir) => DetectInstallKind(dir) == ServerInstallKind.Windows;

    /// <summary>
    /// Which platform's server a folder holds. Windows is the exe we launch; Linux is recognized by the
    /// <c>PalServer.sh</c> the official docs tell Linux users to run, so a server brought over from a Linux host
    /// (or a NAS) gets a straight answer instead of "that isn't a server folder".
    /// </summary>
    public static ServerInstallKind DetectInstallKind(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return ServerInstallKind.None;
        try
        {
            if (File.Exists(Path.Combine(dir, "Pal", "Binaries", "Win64", ProcessScanner.ServerProcessName + ".exe")))
                return ServerInstallKind.Windows;
            if (File.Exists(Path.Combine(dir, "PalServer.sh")) || Directory.Exists(Path.Combine(dir, "Pal", "Binaries", "Linux")))
                return ServerInstallKind.Linux;
            return ServerInstallKind.None;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return ServerInstallKind.None;
        }
    }

    /// <summary>
    /// Copy the world and the settings out of <paramref name="source"/> into a server install at
    /// <paramref name="dest"/>, translating the platform-named config folder. Used for a Linux source, whose
    /// binaries are useless here, so the launcher installs the Windows server itself and takes only this.
    /// Returns what was found, so the caller can say what did and didn't come across.
    /// </summary>
    public static async Task<ImportedPayload> CopyWorldAndSettingsAsync(
        string source, string dest, string configRelative, IProgress<string>? progress, CancellationToken ct)
    {
        var saves = Path.Combine(source, SaveGamesRelative);
        var foundSaves = Directory.Exists(saves);
        if (foundSaves)
            await CopyDirectoryAsync(saves, Path.Combine(dest, SaveGamesRelative), progress, ct).ConfigureAwait(false);

        var configFiles = new List<string>();
        foreach (var name in PortableConfigFiles)
        {
            ct.ThrowIfCancellationRequested();
            var from = Path.Combine(source, configRelative, name);
            if (!File.Exists(from))
                continue;
            var to = Path.Combine(dest, WindowsConfigRelative, name);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            await CopyFileAsync(from, to, ct).ConfigureAwait(false);
            configFiles.Add(name);
        }

        return new ImportedPayload(foundSaves, configFiles);
    }

    /// <summary>
    /// Recursively copy every file under <paramref name="source"/> into <paramref name="dest"/>, overwriting,
    /// and report progress as "copied / total" file counts (throttled so the log isn't flooded). Cancellable.
    /// </summary>
    public static async Task CopyDirectoryAsync(string source, string dest, IProgress<string>? progress, CancellationToken ct)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList();
        var total = files.Count;
        var done = 0;
        var lastReport = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(dest, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await CopyFileAsync(file, target, ct).ConfigureAwait(false);

            done++;
            if (done - lastReport >= 250 || done == total)
            {
                progress?.Report($"Copying server files... {done}/{total}");
                lastReport = done;
            }
        }
    }

    private static async Task CopyFileAsync(string source, string dest, CancellationToken ct)
    {
        const int bufferSize = 1 << 20; // 1 MiB, big enough that a multi-GB install copies without churn
        await using var src = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);
        await using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
        await src.CopyToAsync(dst, ct).ConfigureAwait(false);
    }
}

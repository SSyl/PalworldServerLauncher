using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// Copies an existing (non-launcher) Palworld dedicated server install into the launcher's managed folder,
/// leaving the original untouched. The source is verified to look like a real install first. Version-agnostic:
/// it copies whatever files are there, so it works for any game version.
/// </summary>
public static class ServerImporter
{
    /// <summary>True when <paramref name="dir"/> looks like a Palworld dedicated server install, i.e. it holds
    /// the console server exe at its usual sub-path. Name-independent, so the folder can be called anything.</summary>
    public static bool LooksLikeServerInstall(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return false;
        try
        {
            return File.Exists(Path.Combine(dir, "Pal", "Binaries", "Win64", ProcessScanner.ServerProcessName + ".exe"));
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException)
        {
            return false;
        }
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

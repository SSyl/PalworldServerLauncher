using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>
/// Wraps SteamCMD for installing/updating the Palworld dedicated server and for querying the
/// latest available build id. Version-agnostic: it always operates on app-id 2394010, which
/// resolves to whatever the current build is (0.x / 1.0 / future), see [[launcher-version-agnostic]].
///
/// Install/update runs in SteamCMD's own console window so the user sees its live progress bar
/// (line-by-line redirection hides SteamCMD's carriage-return progress updates). The build-id
/// query runs hidden with captured output because we need to parse it.
/// </summary>
public sealed class SteamCmd
{
    public const string AppId = "2394010";
    private const string SteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private readonly string _serverRoot;

    public SteamCmd(string serverRoot) => _serverRoot = Path.GetFullPath(serverRoot);

    public string SteamCmdDir => Path.Combine(_serverRoot, "steamcmd");
    public string SteamCmdExe => Path.Combine(SteamCmdDir, "steamcmd.exe");
    public string InstallDir => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName);
    public string AppManifestPath => Path.Combine(InstallDir, "steamapps", $"appmanifest_{AppId}.acf");

    /// <summary>SteamCMD's own console log, tailed into the SteamCMD tab while it runs in its window.</summary>
    public string ConsoleLogPath => Path.Combine(SteamCmdDir, "logs", "console_log.txt");

    /// <summary>Download + unzip + prime SteamCMD if it isn't present yet. Small (~few MB) download.</summary>
    public async Task EnsureSteamCmdAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        if (File.Exists(SteamCmdExe))
            return;

        log?.Report("SteamCMD not found. Downloading...");
        Directory.CreateDirectory(SteamCmdDir);
        var zipPath = Path.Combine(SteamCmdDir, "steamcmd.zip");

        using (var http = new HttpClient())
        await using (var src = await http.GetStreamAsync(SteamCmdUrl, ct).ConfigureAwait(false))
        await using (var dst = File.Create(zipPath))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);

        ZipFile.ExtractToDirectory(zipPath, SteamCmdDir, overwriteFiles: true);
        File.Delete(zipPath);
        log?.Report("SteamCMD installed. Priming (a console window will open)...");

        // First run primes SteamCMD's own self-update; shown so the user sees it working.
        await RunProcessAsync(["+login", "anonymous", "+quit"], visible: true, log, ct).ConfigureAwait(false);
        log?.Report("SteamCMD ready.");
    }

    /// <summary>
    /// Install or update the server in place. SteamCMD delta-patches and never touches user files
    /// under Saved/. force_install_dir is passed before login (a SteamCMD requirement) and +logoff
    /// before +quit (works around occasional never-ending sessions).
    /// <paramref name="validate"/> adds a full file-integrity pass (slower); <paramref name="visible"/>
    /// runs in SteamCMD's own console window (the caller tails <see cref="ConsoleLogPath"/> either way).
    /// </summary>
    public Task<int> InstallOrUpdateServerAsync(bool validate, bool visible, IProgress<string>? log, CancellationToken ct = default)
    {
        var args = new List<string> { "+force_install_dir", InstallDir, "+login", "anonymous", "+app_update", AppId };
        if (validate)
            args.Add("validate");
        args.AddRange(["+logoff", "+quit"]);
        return RunProcessAsync(args, visible, log, ct);
    }

    /// <summary>Query the latest published build id from Steam (null if it can't be determined).</summary>
    public async Task<string?> QueryLatestBuildIdAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        var (_, output) = await RunCapturedAsync(
            ["+login", "anonymous", "+app_info_update", "1", "+app_info_print", AppId, "+logoff", "+quit"], ct)
            .ConfigureAwait(false);
        return ParseBuildId(output);
    }

    /// <summary>Build id currently installed on disk, read from the app manifest (null if not installed).</summary>
    public string? ReadInstalledBuildId() =>
        File.Exists(AppManifestPath) ? ParseBuildId(File.ReadAllText(AppManifestPath)) : null;

    /// <summary>
    /// Extract the first <c>"buildid" "N"</c> value from ACF/app_info text. In app_info_print the
    /// public branch's buildid appears first, which is the one we compare against, matching the
    /// proven behavior of the original Python tool (start_server.py:218).
    /// </summary>
    public static string? ParseBuildId(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!line.Contains("\"buildid\"", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = line.Split('"');
            // Expected: ["", "buildid", "\t\t", "12345", ""] -> value at index 3.
            if (parts.Length >= 4 && !string.IsNullOrWhiteSpace(parts[3]))
                return parts[3].Trim();
        }
        return null;
    }

    /// <summary>
    /// Run SteamCMD to completion. Output isn't captured (its carriage-return progress bar doesn't
    /// survive a redirected pipe); progress reaches the UI by tailing <see cref="ConsoleLogPath"/>,
    /// which SteamCMD writes whether or not it has a console window. <paramref name="visible"/> gives
    /// it its own console window (reassuring during the slow "Verifying..." pass); hidden keeps it out
    /// of the way for the frequent Start-time update.
    /// </summary>
    private async Task<int> RunProcessAsync(IReadOnlyList<string> args, bool visible, IProgress<string>? log, CancellationToken ct)
    {
        log?.Report($"Running SteamCMD: {SteamCmdExe} {string.Join(' ', args.Select(Quote))}");
        var started = DateTime.Now;

        var psi = new ProcessStartInfo
        {
            FileName = SteamCmdExe,
            WorkingDirectory = SteamCmdDir,
            UseShellExecute = visible,          // true gives SteamCMD its own console window
            CreateNoWindow = !visible,
            WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        log?.Report($"SteamCMD finished (exit {process.ExitCode}, {(DateTime.Now - started).TotalSeconds:F0}s).");
        return process.ExitCode;
    }

    /// <summary>Run SteamCMD hidden, capturing stdout+stderr (for the build-id query).</summary>
    private async Task<(int ExitCode, string Output)> RunCapturedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SteamCmdExe,
            WorkingDirectory = SteamCmdDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        var output = new StringBuilder();
        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, output.ToString());
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}

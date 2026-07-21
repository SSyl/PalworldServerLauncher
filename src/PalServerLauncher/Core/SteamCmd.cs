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
    /// <summary>Palworld's game app id, where Steam Workshop content lives (distinct from the server <see cref="AppId"/>).</summary>
    public const string GameAppId = "1623730";
    private const string SteamCmdUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    private readonly string _serverRoot;

    public SteamCmd(string serverRoot) => _serverRoot = Path.GetFullPath(serverRoot);

    public string SteamCmdDir => Path.Combine(_serverRoot, "steamcmd");
    public string SteamCmdExe => Path.Combine(SteamCmdDir, "steamcmd.exe");
    public string InstallDir => Path.Combine(_serverRoot, LauncherConfig.ServerFolderName);
    public string AppManifestPath => Path.Combine(InstallDir, "steamapps", $"appmanifest_{AppId}.acf");

    /// <summary>SteamCMD's own console log, tailed into the SteamCMD tab while it runs in its window.</summary>
    public string ConsoleLogPath => Path.Combine(SteamCmdDir, "logs", "console_log.txt");

    /// <summary>Where <c>workshop_download_item</c> lands a mod, before the launcher copies it into the server's Mods\Workshop.</summary>
    public string WorkshopContentDir(string workshopId) =>
        Path.Combine(SteamCmdDir, "steamapps", "workshop", "content", GameAppId, workshopId);

    /// <summary>SteamCMD's Workshop state file, tracking each installed item's content manifest and last-updated time.</summary>
    public string WorkshopAcfPath =>
        Path.Combine(SteamCmdDir, "steamapps", "workshop", $"appworkshop_{GameAppId}.acf");

    /// <summary>The cache state (content manifest + last-updated) SteamCMD has for one Workshop item, or null if the
    /// ACF is missing/unreadable or has no entry for it. Powers the update-detection gate, which re-copies a mod into
    /// the server only when its manifest advances.</summary>
    public WorkshopManifest.ItemState? ReadWorkshopItemState(string workshopId)
    {
        if (!File.Exists(WorkshopAcfPath))
            return null;
        try
        {
            var items = WorkshopManifest.ParseInstalled(File.ReadAllText(WorkshopAcfPath));
            return items.TryGetValue(workshopId, out var state) ? state : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Download + unzip + prime SteamCMD if it isn't present yet. Small (~few MB) download. No-op when
    /// it's already there, so it's safe to call before any SteamCMD operation to self-heal a missing install
    /// (e.g. a server imported or hand-placed without SteamCMD). <paramref name="visible"/> shows the priming
    /// run in its own console window (reassuring during an explicit install, suppressed for a silent build-id check).</summary>
    public async Task EnsureSteamCmdAsync(IProgress<string>? log, CancellationToken ct = default, bool visible = true)
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
        log?.Report(visible ? "SteamCMD installed. Priming (a console window will open)..." : "SteamCMD installed. Priming...");

        // First run primes SteamCMD's own self-update.
        await RunProcessAsync(["+login", "anonymous", "+quit"], visible, log, ct).ConfigureAwait(false);
        log?.Report("SteamCMD ready.");
    }

    /// <summary>
    /// Install or update the server in place. SteamCMD delta-patches and never touches user files
    /// under Saved/. force_install_dir is passed before login (a SteamCMD requirement) and +logoff
    /// before +quit (works around occasional never-ending sessions).
    /// <paramref name="validate"/> adds a full file-integrity pass (slower); <paramref name="visible"/>
    /// runs in SteamCMD's own console window (the caller tails <see cref="ConsoleLogPath"/> either way).
    /// </summary>
    public async Task<int> InstallOrUpdateServerAsync(bool validate, bool visible, IProgress<string>? log, CancellationToken ct = default)
    {
        var args = new List<string> { "+force_install_dir", InstallDir, "+login", "anonymous", "+app_update", AppId };
        if (validate)
            args.Add("validate");
        args.AddRange(["+logoff", "+quit"]);
        try
        {
            return await RunProcessAsync(args, visible, log, ct).ConfigureAwait(false);
        }
        finally
        {
            InvalidateBuildIdCache(); // the manifest may now hold a new build id (invalidate even on a partial run)
        }
    }

    /// <summary>Query the latest published build id from Steam (null if it can't be determined).</summary>
    public async Task<string?> QueryLatestBuildIdAsync(IProgress<string>? log, CancellationToken ct = default)
    {
        var (_, output) = await RunCapturedAsync(
            ["+login", "anonymous", "+app_info_update", "1", "+app_info_print", AppId, "+logoff", "+quit"], ct)
            .ConfigureAwait(false);
        return ParseBuildId(output);
    }

    /// <summary>The outcome of a Workshop download run.</summary>
    public enum WorkshopDownloadResult { Ok, AuthFailed, Failed }

    /// <summary>
    /// Interactive one-time Steam sign-in for Workshop downloads. Launches SteamCMD directly in its OWN console
    /// window with <c>+login &lt;username&gt; &lt;password&gt; +quit</c>, so the user never faces SteamCMD's blank,
    /// no-echo password prompt (the thing that trips people up). Steam Guard is still handled IN THAT WINDOW: a
    /// mobile-authenticator account shows "confirm on your phone", an email/code account prompts for the (echoed)
    /// code. On a successful login SteamCMD caches its own session, so later downloads pass only the username. The
    /// launcher confirms success separately via <see cref="HasCachedSessionAsync"/>, so this method's exit code is
    /// unused.
    ///
    /// The password goes through <see cref="ProcessStartInfo.ArgumentList"/> (no shell), so symbols in it can't
    /// break quoting or inject a command, and it is deliberately never logged or persisted anywhere.
    /// </summary>
    public async Task ConnectAccountAsync(string username, string password, IProgress<string>? log, CancellationToken ct = default)
    {
        username = username.Trim();
        log?.Report($"Opening a Steam sign-in window for account '{username}'. If prompted, approve the sign-in on your phone or enter your Steam Guard code there.");

        var psi = new ProcessStartInfo
        {
            FileName = SteamCmdExe,
            WorkingDirectory = SteamCmdDir,
            UseShellExecute = false,
            CreateNoWindow = false,          // a console app launched from the windowless GUI gets its own window
            WindowStyle = ProcessWindowStyle.Normal,
        };
        // Never log these args: ArgumentList[2] is the password.
        psi.ArgumentList.Add("+login");
        psi.ArgumentList.Add(username);
        psi.ArgumentList.Add(password);
        psi.ArgumentList.Add("+quit");

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        log?.Report("Steam sign-in window closed, verifying the session...");
    }

    /// <summary>
    /// Confirm SteamCMD has a usable cached session for <paramref name="username"/> by running a hidden captured
    /// login (stdin is closed, so a missing/expired session fails fast instead of prompting). True when the login
    /// didn't hit an auth failure, i.e. the cached session works. Used right after <see cref="ConnectAccountAsync"/>
    /// to reliably tell the user whether the sign-in took.
    /// </summary>
    public async Task<bool> HasCachedSessionAsync(string username, CancellationToken ct = default)
    {
        var (_, output) = await RunCapturedAsync(["+login", username, "+quit"], ct).ConfigureAwait(false);
        return !LooksLikeAuthFailure(output);
    }

    /// <summary>
    /// Download (or update, it's incremental) one Workshop item using SteamCMD's cached session. No password is
    /// passed, and if the session has expired SteamCMD's stdin is closed so it fails fast instead of hanging, and
    /// we report <see cref="WorkshopDownloadResult.AuthFailed"/> so the caller can ask the user to reconnect.
    /// </summary>
    public async Task<WorkshopDownloadResult> DownloadWorkshopItemAsync(
        string username, string workshopId, IProgress<string>? log, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(10)); // a hung download must not block a start forever
        int exit;
        string output;
        try
        {
            (exit, output) = await RunCapturedAsync(
                ["+login", username, "+workshop_download_item", GameAppId, workshopId, "+quit"], timeout.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log?.Report($"Workshop download of {workshopId} timed out.");
            return WorkshopDownloadResult.Failed;
        }

        if (output.Contains("Success. Downloaded item", StringComparison.OrdinalIgnoreCase))
        {
            log?.Report($"Downloaded Workshop item {workshopId}.");
            return WorkshopDownloadResult.Ok;
        }
        if (LooksLikeAuthFailure(output))
        {
            log?.Report($"Workshop download of {workshopId} needs a Steam sign-in (reconnect your account).");
            return WorkshopDownloadResult.AuthFailed;
        }
        log?.Report($"Workshop download of {workshopId} failed (exit {exit}).");
        return WorkshopDownloadResult.Failed;
    }

    private static bool LooksLikeAuthFailure(string output) =>
        output.Contains("Login Failure", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Invalid Password", StringComparison.OrdinalIgnoreCase)
        || output.Contains("password:", StringComparison.OrdinalIgnoreCase)       // prompted = no cached session
        || output.Contains("Two-factor code", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Steam Guard", StringComparison.OrdinalIgnoreCase)
        || (output.Contains("FAILED", StringComparison.OrdinalIgnoreCase) && output.Contains("login", StringComparison.OrdinalIgnoreCase));

    private readonly object _buildIdLock = new();
    private string? _cachedBuildId;
    private bool _buildIdCached;

    /// <summary>Build id currently installed on disk, parsed from the app manifest (null if not installed).
    /// Cached in memory: the manifest only changes on an app_update or a server import, both of which invalidate
    /// the cache (<see cref="InvalidateBuildIdCache"/>), so the health probe's per-tick reads don't touch the
    /// disk on a long-running server.</summary>
    public string? ReadInstalledBuildId()
    {
        lock (_buildIdLock)
        {
            if (!_buildIdCached)
            {
                _cachedBuildId = File.Exists(AppManifestPath) ? ParseBuildId(File.ReadAllText(AppManifestPath)) : null;
                _buildIdCached = true;
            }
            return _cachedBuildId;
        }
    }

    /// <summary>Drop the cached build id so the next <see cref="ReadInstalledBuildId"/> re-parses the manifest.
    /// Call after anything that can change the installed build: a SteamCMD app_update or a server import.</summary>
    public void InvalidateBuildIdCache()
    {
        lock (_buildIdLock)
            _buildIdCached = false;
    }

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
            RedirectStandardInput = true,   // closed right after start, so a login prompt gets EOF instead of hanging
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
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return (process.ExitCode, output.ToString());
    }

    private static string Quote(string arg) => arg.Contains(' ') ? $"\"{arg}\"" : arg;
}

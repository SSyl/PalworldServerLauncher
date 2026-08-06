using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>
/// Recovers why the server died from Unreal's crash context. Palworld writes no log file and its fatal
/// errors never reach stdout, so <c>Pal/Saved/Crashes/UECC-*/CrashContext.runtime-xml</c> is the only
/// record of the reason. A corrupt world, for one, reports "Save data is corrupted. Please restore from
/// a backup" there while the captured Server Log just stops mid-startup with no error at all (issue #11).
/// </summary>
public static class CrashReport
{
    public const string ContextFileName = "CrashContext.runtime-xml";

    /// <summary>Unreal's crash handler runs in-process and writes the context about 200ms BEFORE the
    /// process exits (measured against Palworld 1.0.2), so the file is already complete by the time
    /// Process.Exited fires. These retries only cover a slow disk, they are not the expected path.</summary>
    private const int ReadAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    public static string CrashesDir(string serverRoot) =>
        Path.Combine(LauncherConfig.ServerDir(serverRoot), "Pal", "Saved", "Crashes");

    /// <summary>
    /// Crash folders written at or after <paramref name="launchedUtc"/>, newest first. The cutoff is what
    /// keeps an old crash from being blamed on the current one: a force-stop or an external kill writes no
    /// folder at all, so the newest folder on disk can be days stale.
    /// </summary>
    public static IEnumerable<string> SelectCrashDirs(
        IEnumerable<(string Path, DateTime WrittenUtc)> dirs, DateTime launchedUtc) =>
        dirs.Where(dir => dir.WrittenUtc >= launchedUtc)
            .OrderByDescending(dir => dir.WrittenUtc)
            .Select(dir => dir.Path);

    /// <summary>
    /// Candidate folders stamped by their context file rather than the folder. A folder's timestamp moves
    /// whenever anything inside it changes, so an old crash that something else touches mid-run (an upload,
    /// a dump moved out to send to support) would otherwise look newer than the real one. The context file
    /// is written once by the dying process and never touched again. Folders without one keep the directory
    /// stamp, so a crash caught before the context landed is still a candidate.
    /// </summary>
    private static IEnumerable<(string Path, DateTime WrittenUtc)> EnumerateCandidates(string root) =>
        Directory.EnumerateDirectories(root).Select(dir =>
        {
            var context = Path.Combine(dir, ContextFileName);
            return (dir, File.Exists(context) ? File.GetLastWriteTimeUtc(context) : Directory.GetLastWriteTimeUtc(dir));
        });

    /// <summary>
    /// The reason text out of a crash context, or null when it isn't there. Pulled out by substring rather
    /// than parsed as XML on purpose: the file is written by a process in the middle of dying, so a
    /// truncated one has to come back empty instead of throwing. Whitespace collapses because Unreal writes
    /// multi-line text for some asserts and the log is read a line at a time.
    /// </summary>
    public static string? ExtractErrorMessage(string xml)
    {
        const string open = "<ErrorMessage>";
        const string close = "</ErrorMessage>";

        var start = xml.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += open.Length;

        var end = xml.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0)
            return null;

        var text = Regex.Replace(WebUtility.HtmlDecode(xml[start..end]), @"\s+", " ").Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>
    /// One line describing how the server died. A death during startup is a different problem from one after
    /// hours of play (a bad save or config versus a runtime fault), so the phase leads. Exit codes measured
    /// on Palworld 1.0.2: 0 clean, 3 fatal assert, 1 external kill. English on purpose, the log isn't localized.
    /// </summary>
    public static string DescribeExit(int? exitCode, TimeSpan uptime, bool reachedRunning)
    {
        var code = exitCode is null ? "" : $" (exit code {exitCode})";
        return reachedRunning
            ? $"Server exited unexpectedly after {FormatUptime(uptime)}{code}."
            : $"Server exited unexpectedly during startup, {FormatUptime(uptime)} after launch{code}.";
    }

    /// <summary>Sub-second precision below a minute, because a startup crash lands a couple of seconds in
    /// and "0m 6s" hides the part that identifies it.</summary>
    public static string FormatUptime(TimeSpan uptime) => uptime.TotalSeconds switch
    {
        < 60 => $"{uptime.TotalSeconds:0.0}s",
        < 3600 => $"{uptime.Minutes}m {uptime.Seconds}s",
        _ => $"{(int)uptime.TotalHours}h {uptime.Minutes}m",
    };

    /// <summary>
    /// What this run's crash left behind: the folder, plus the reason when the context could be read. Null
    /// only when the run wrote no crash folder at all (a force-stop, or a death Unreal never caught).
    /// Reason and Directory are separate because they fail separately: a context truncated by the dying
    /// process yields a folder with no reason, and that folder still holds the minidump worth pointing at.
    /// Never throws, an unreadable folder just means less to report.
    /// </summary>
    public static async Task<(string? Reason, string Directory)?> ReadAsync(string serverRoot, DateTime launchedUtc)
    {
        (string? Reason, string Directory)? found = null;

        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(RetryDelay).ConfigureAwait(false);

            try
            {
                var root = CrashesDir(serverRoot);
                if (!Directory.Exists(root))
                    continue;

                // Newest first, and keep walking: if the newest folder's context is unreadable, an older
                // folder from this same run may still carry the reason.
                foreach (var dir in SelectCrashDirs(EnumerateCandidates(root), launchedUtc))
                {
                    found ??= (null, dir);
                    var contextPath = Path.Combine(dir, ContextFileName);
                    if (!File.Exists(contextPath))
                        continue;

                    var reason = ExtractErrorMessage(await File.ReadAllTextAsync(contextPath).ConfigureAwait(false));
                    if (reason is not null)
                        return (reason, dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
            {
                // Mid-write, locked, or a path we can't touch. Fall through to the next attempt.
            }
        }

        return found;

        return null;
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>
/// Locates the managed Palworld server process by matching the console server exe whose on-disk
/// path lives under a specific server root, this is what makes the launcher stateless and
/// re-attachable (it can adopt a server it did not start) and lets multiple servers coexist as
/// long as they are in separate folders. Also exposes process I/O counters, used by the health
/// monitor as an OS-level "is it actually doing anything" signal when the REST API is unresponsive.
/// </summary>
public static class ProcessScanner
{
    /// <summary>Process name (no extension) of the console dedicated server we manage.</summary>
    public const string ServerProcessName = "PalServer-Win64-Shipping-Cmd";

    /// <summary>Full path to the managed server exe for a given root.</summary>
    public static string ExpectedExePath(string serverRoot) =>
        Path.GetFullPath(Path.Combine(serverRoot, LauncherConfig.ServerFolderName, "Pal", "Binaries", "Win64", ServerProcessName + ".exe"));

    /// <summary>
    /// Return a running managed server process whose exe lives under <paramref name="serverRoot"/>,
    /// or null. Caller owns the returned <see cref="Process"/> and should dispose it.
    /// </summary>
    public static Process? FindManagedServer(string serverRoot)
    {
        var all = FindAllManagedServers(serverRoot);
        if (all.Count == 0)
            return null;

        for (var i = 1; i < all.Count; i++)
            all[i].Dispose(); // caller only wants the primary; drop handles to the rest

        return all[0];
    }

    /// <summary>
    /// Return every running managed server process whose exe lives under <paramref name="serverRoot"/>
    /// (there should normally be one, but orphans/duplicates can accumulate). Caller owns and disposes
    /// the returned <see cref="Process"/> objects.
    /// </summary>
    public static IReadOnlyList<Process> FindAllManagedServers(string serverRoot)
    {
        var root = NormalizeDir(serverRoot);
        var matches = new List<Process>();

        foreach (var candidate in Process.GetProcessesByName(ServerProcessName))
        {
            var matched = false;
            try
            {
                var path = candidate.MainModule?.FileName;
                matched = path is not null && IsUnder(path, root);
            }
            catch
            {
                // Access denied / process exited between enumeration and inspection, not ours.
            }

            if (matched)
                matches.Add(candidate);
            else
                candidate.Dispose();
        }

        return matches;
    }

    /// <summary>True when <paramref name="filePath"/> sits inside <paramref name="directory"/> (case-insensitive, boundary-safe).</summary>
    public static bool IsUnder(string filePath, string directory)
    {
        var full = Path.GetFullPath(filePath);
        var dir = NormalizeDir(directory);
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDir(string dir)
    {
        var full = Path.GetFullPath(dir);
        if (!full.EndsWith(Path.DirectorySeparatorChar))
            full += Path.DirectorySeparatorChar;
        return full;
    }

    /// <summary>Whether a running server process is one we manage (path under our root), a foreign one (readable
    /// path outside our root), or one whose path we couldn't read (so we can neither confirm it is ours nor attach
    /// to it, meaning a Start would spawn a competing duplicate).</summary>
    public enum ServerOwnership { Managed, Foreign, Unreadable }

    /// <summary>Classify a server process by its exe path relative to our root. A null/blank path (MainModule
    /// unreadable, e.g. it is running elevated) is <see cref="ServerOwnership.Unreadable"/>. Pure and unit-tested.</summary>
    public static ServerOwnership ClassifyServerPath(string? exePath, string serverRoot)
    {
        if (string.IsNullOrWhiteSpace(exePath))
            return ServerOwnership.Unreadable;
        return IsUnder(exePath, serverRoot) ? ServerOwnership.Managed : ServerOwnership.Foreign;
    }

    /// <summary>A running Palworld server the launcher does not manage: <see cref="Path"/> is the exe path for
    /// a foreign install, or null when it couldn't be read.</summary>
    public readonly record struct UnmanagedServer(int Pid, string? Path);

    /// <summary>
    /// Every running Palworld server process that is NOT managed under <paramref name="serverRoot"/> (Foreign or
    /// Unreadable). Starting a server while one of these runs risks a port conflict or a competing duplicate.
    /// Handles are disposed internally, terminate by <see cref="UnmanagedServer.Pid"/> via <see cref="TryTerminate"/>.
    /// </summary>
    public static IReadOnlyList<UnmanagedServer> FindUnmanagedServers(string serverRoot)
    {
        var result = new List<UnmanagedServer>();
        foreach (var candidate in Process.GetProcessesByName(ServerProcessName))
        {
            try
            {
                string? path = null;
                try { path = candidate.MainModule?.FileName; }
                catch { /* access denied / exited */ }
                if (string.IsNullOrWhiteSpace(path))
                {
                    // A MainModule read can transiently fail while a process is still building its module list. Wait
                    // briefly and read once more so our own server is not misclassified Unreadable on a momentary glitch.
                    // A persistent failure (e.g. an elevated process we can't open) stays Unreadable, which is correct.
                    // This only runs for a process we couldn't read (rare), so the short wait is not felt.
                    Thread.Sleep(150);
                    try { path = candidate.MainModule?.FileName; }
                    catch { }
                }

                if (ClassifyServerPath(path, serverRoot) != ServerOwnership.Managed)
                    result.Add(new UnmanagedServer(candidate.Id, path));
            }
            finally
            {
                candidate.Dispose();
            }
        }
        return result;
    }

    /// <summary>What happened when we tried to terminate a server pid.</summary>
    public enum TerminateResult
    {
        /// <summary>We killed the process tree and it exited.</summary>
        Killed,
        /// <summary>The target was already gone (it had exited, or its pid was recycled onto another process).</summary>
        AlreadyGone,
        /// <summary>We could not kill it, see the error (Access Denied on an elevated server, or it did not exit in time).</summary>
        Failed,
    }

    /// <summary>Terminate a server process by pid. <see cref="TerminateResult.Failed"/> sets <paramref name="error"/>
    /// (e.g. Access Denied because it is running elevated, or it did not exit after being killed). A process that is
    /// already gone is <see cref="TerminateResult.AlreadyGone"/>, not a failure.</summary>
    public static TerminateResult TryTerminate(int pid, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.GetProcessById(pid);
            // The pid was captured at scan time and the handle released, so between the prompt and now the process
            // could have exited and Windows recycled the pid onto something unrelated. Never kill a pid that is no
            // longer a Palworld server.
            if (!process.ProcessName.Equals(ServerProcessName, StringComparison.OrdinalIgnoreCase))
                return TerminateResult.AlreadyGone; // the pid now belongs to a different process
            process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(5000))
            {
                error = "the process did not exit within 5 seconds of being killed";
                return TerminateResult.Failed;
            }
            return TerminateResult.Killed;
        }
        catch (ArgumentException)
        {
            return TerminateResult.AlreadyGone; // no such process
        }
        catch (InvalidOperationException)
        {
            return TerminateResult.AlreadyGone; // process exited between scan and kill
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            error = ex.Message; // e.g. Access Denied on an elevated process
            return TerminateResult.Failed;
        }
    }

    /// <summary>How a caller should report a <see cref="TryTerminate"/> outcome: whether Start may proceed
    /// (<see cref="Succeeded"/>), whether to log it as an error, and the message.</summary>
    public readonly record struct TerminateReport(bool Succeeded, bool IsError, string LogMessage);

    /// <summary>Map a <see cref="TryTerminate"/> result to a log message and a proceed/abort decision. Already-gone
    /// counts as success (the goal, that server not running, is met), only <see cref="TerminateResult.Failed"/> aborts
    /// Start. Pure and unit-tested.</summary>
    public static TerminateReport DescribeTerminate(TerminateResult result, int pid, string? error) => result switch
    {
        TerminateResult.Killed => new(true, false, $"Terminated an unmanaged server process (PID {pid})."),
        TerminateResult.AlreadyGone => new(true, false, $"Unmanaged server process (PID {pid}) was already gone, nothing to terminate."),
        TerminateResult.Failed => new(false, true, $"Couldn't terminate server process PID {pid}: {error}"),
        _ => new(false, true, $"Unexpected terminate result '{result}' for PID {pid}."),
    };

    /// <summary>Read the process I/O counters, or null if the handle can't be queried.</summary>
    public static IoCounters? TryGetIoCounters(Process process)
    {
        try
        {
            if (GetProcessIoCounters(process.Handle, out var counters))
                return counters;
        }
        catch (InvalidOperationException)
        {
            // Process already exited, no handle to query.
        }
        return null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessIoCounters(IntPtr hProcess, out IoCounters counters);

    [StructLayout(LayoutKind.Sequential)]
    public struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;

        /// <summary>
        /// Bytes read + written, excluding "other" I/O. The Conan DSL learned to ignore Other
        /// because a server stuck on shutdown still ticks Other I/O, causing false "alive" reads
        /// (ConanExilesDedicatedServerLauncher.txt:761). This is the value we watch for a stall.
        /// </summary>
        public readonly ulong ReadWriteBytes => ReadTransferCount + WriteTransferCount;
    }
}

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>
/// Locates the managed Palworld server process by matching the console server exe whose on-disk
/// path lives under a specific server root - this is what makes the launcher stateless and
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
                // Access denied / process exited between enumeration and inspection - not ours.
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
            // Process already exited - no handle to query.
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

using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>What a running launcher did with a <see cref="StopRequest"/>, reported back to the CLI process.</summary>
public sealed record StopOutcome(bool Succeeded, string Message);

/// <summary>
/// The named pipe a running launcher listens on so a second copy of the exe (<c>--stop-server</c>) can ask it to
/// stop the server, rather than killing the process behind its back. Killing it externally would fire
/// <see cref="ServerController.OnProcessExited"/> with no manual-stop latched, which reads as a crash and
/// relaunches the server. Routing through the owner instead means the stop goes down the normal path and latches
/// <see cref="RelaunchGate"/>.
///
/// Line-based UTF-8. The client sends one <see cref="StopRequest.ToWire"/> line, the launcher replies with zero or
/// more "log &lt;text&gt;" lines and then exactly one terminal "ok &lt;text&gt;" or "err &lt;text&gt;".
/// </summary>
public static class LauncherIpc
{
    public const string LogPrefix = "log ";
    public const string OkPrefix = "ok ";
    public const string ErrPrefix = "err ";

    /// <summary>How long the CLI waits for a running launcher to answer before falling back to a standalone stop.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(750);

    /// <summary>Cap on a single request, so a wedged launcher can't hang a scripted stop forever. Generous because a
    /// graceful stop does a shutdown backup first, which is a full zip of SaveGames and can run for minutes.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Pipe name for one launcher installation, derived from its data root so two copies of the exe in different
    /// folders never talk to each other. Keyed on the data root rather than the (user-movable) server root because
    /// both ends can compute it from their own <see cref="Config.LauncherConfig.DataRoot"/> without reading
    /// launcher.json. Pure, so it is tested.
    /// </summary>
    /// <summary>Whether a launcher currently holds this pipe name. Named pipes surface under the \\.\pipe
    /// pseudo-directory, so existence is a plain file check, and it answers the question a failed connect can't:
    /// was nobody there, or was someone there and unavailable.</summary>
    public static bool IsListening(string pipeName)
    {
        try
        {
            return File.Exists(@"\\.\pipe\" + pipeName);
        }
        catch
        {
            return false;
        }
    }

    public static string PipeNameFor(string dataRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "PalworldServerLauncher." + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>
/// The CLI end: connect to a running launcher and hand it a stop request. Failures are reported as values rather
/// than exceptions, and "no launcher answered" is kept distinct from "a launcher answered and then failed",
/// because only the first makes it safe to go stop the server ourselves.
/// </summary>
public static class LauncherIpcClient
{
    /// <summary>
    /// Send <paramref name="request"/> to a launcher listening on <paramref name="pipeName"/> and return its
    /// outcome. Null means specifically that NO launcher answered, so the caller may safely stop the server
    /// itself. A launcher that answered and then failed comes back as a failed outcome instead: falling back
    /// there would run a second shutdown on top of one already in flight. Progress lines arrive on
    /// <paramref name="onLog"/> as they stream in.
    /// </summary>
    public static async Task<StopOutcome?> SendAsync(string pipeName, StopRequest request, Action<string> onLog)
    {
        // Anonymous impersonation: the server end of a pipe can impersonate its client, and this name is
        // derivable from the install path on a machine where any local user can create it first. Nothing we
        // connect to should get to act as this user.
        await using var pipe = new NamedPipeClientStream(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);

        try
        {
            await pipe.ConnectAsync((int)LauncherIpc.ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            // Failing to connect is not the same as nobody being there. A listener that exists but is busy (it
            // serves one at a time) or unreachable (an elevated launcher, a different session) still owns the
            // server, and stopping it ourselves would look like a crash to it and get the server relaunched.
            // Only a name that isn't there at all licenses the standalone path.
            return LauncherIpc.IsListening(pipeName)
                ? new StopOutcome(false, "A launcher is running but wouldn't accept the request. It may be busy with another stop.")
                : null;
        }

        try
        {
            using var deadline = new CancellationTokenSource(LauncherIpc.RequestTimeout);
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            var reader = new StreamReader(pipe, new UTF8Encoding(false));

            await writer.WriteLineAsync(request.ToWire().AsMemory(), deadline.Token).ConfigureAwait(false);

            while (await reader.ReadLineAsync(deadline.Token).ConfigureAwait(false) is { } line)
            {
                if (line.StartsWith(LauncherIpc.OkPrefix, StringComparison.Ordinal))
                    return new StopOutcome(true, line[LauncherIpc.OkPrefix.Length..]);
                if (line.StartsWith(LauncherIpc.ErrPrefix, StringComparison.Ordinal))
                    return new StopOutcome(false, line[LauncherIpc.ErrPrefix.Length..]);
                if (line.StartsWith(LauncherIpc.LogPrefix, StringComparison.Ordinal))
                    onLog(line[LauncherIpc.LogPrefix.Length..]);
            }

            // The launcher closed the pipe without a terminal line (it exited mid-stop, most likely).
            return new StopOutcome(false, "The launcher closed the connection before finishing the stop.");
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return new StopOutcome(false, $"Lost contact with the running launcher mid-stop: {ex.Message}");
        }
    }
}

using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
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
    public static string PipeNameFor(string dataRoot)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToLowerInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "PalworldServerLauncher." + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }
}

/// <summary>
/// The CLI end: connect to a running launcher and hand it a stop request. Every failure to reach one is reported
/// as null (not an exception) so the caller can fall back to stopping the server standalone.
/// </summary>
public static class LauncherIpcClient
{
    /// <summary>
    /// Send <paramref name="request"/> to a launcher listening on <paramref name="pipeName"/>. Returns its outcome,
    /// or null when no launcher answered (nothing listening, or a stop already in flight holding the instance).
    /// Progress lines arrive on <paramref name="onLog"/> as they stream in.
    /// </summary>
    public static async Task<StopOutcome?> SendAsync(string pipeName, StopRequest request, Action<string> onLog)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync((int)LauncherIpc.ConnectTimeout.TotalMilliseconds).ConfigureAwait(false);

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
        catch (TimeoutException)
        {
            return null; // nothing listening
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            onLog($"Could not talk to the running launcher: {ex.Message}");
            return null;
        }
    }
}

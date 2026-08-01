using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// The launcher end of <see cref="LauncherIpc"/>: listens for a <c>--stop-server</c> from a second copy of the exe
/// and runs it through the handler (which routes to the normal stop path, so the relaunch latch is set).
///
/// One connection at a time, on purpose. Stops are rare and user-driven, and a serial loop means a long graceful
/// stop can't be raced by a second request. That makes the listener's liveness critical: every per-connection
/// failure has to end the connection and keep looping, never fall out of the accept loop.
/// </summary>
public sealed class LauncherIpcServer : IDisposable
{
    /// <summary>How long a connected client gets to send its request line before it is dropped.</summary>
    public static readonly TimeSpan DefaultRequestReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long one reply line may block on a client that isn't reading. The pipe is created with a zero
    /// output buffer, so every write waits for the client, and a client that stops reading would otherwise hold
    /// the single pipe instance forever.</summary>
    public static readonly TimeSpan DefaultWriteTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Cap on queued progress lines. Reached only when the client has stopped reading, in which case the
    /// connection is already being abandoned, so dropping the overflow is right.</summary>
    private const int MaxQueuedLines = 512;

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _pipeName;
    private readonly Func<StopRequest, Action<string>, Task<StopOutcome>> _handler;
    private readonly Action<string> _log;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _ownsPipeName;

    /// <param name="handler">Runs the request. Its second argument is a progress sink, safe to call from any
    /// thread and guaranteed not to block the caller.</param>
    public LauncherIpcServer(
        string pipeName,
        Func<StopRequest, Action<string>, Task<StopOutcome>> handler,
        Action<string> log,
        TimeSpan? requestReadTimeout = null,
        TimeSpan? writeTimeout = null)
    {
        _pipeName = pipeName;
        _handler = handler;
        _log = log;
        _readTimeout = requestReadTimeout ?? DefaultRequestReadTimeout;
        _writeTimeout = writeTimeout ?? DefaultWriteTimeout;
    }

    /// <summary>Begin listening. Never throws: a pipe we can't create just means no CLI control this session.</summary>
    public void Start() => _loop ??= Task.Run(() => ListenAsync(_cts.Token));

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe(_pipeName, claimName: !_ownsPipeName);
                _ownsPipeName = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // FirstPipeInstance refused the name: someone else already holds it. That is either a second
                // launcher on this same data root (they already conflict over the server itself) or a local
                // process squatting the name. Either way, don't fight for it and don't retry-spam.
                _log("Couldn't claim the CLI stop listener, another process already holds the name. " +
                     "This launcher won't accept command-line stops.");
                return;
            }
            catch (Exception ex)
            {
                _log($"CLI stop listener unavailable: {ex.Message}");
                return;
            }

            try
            {
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await ServeAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down, the only reason to leave the loop
            }
            catch (Exception ex)
            {
                // Anything else is this ONE connection's problem (a timeout, a client that vanished mid-reply).
                // Log it and keep listening: a dead listener silently sends every later --stop-server down the
                // standalone path, which is exactly the crash-relaunch this feature exists to avoid.
                _log($"CLI stop request failed: {ex.Message}");
            }
            finally
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var reader = new StreamReader(pipe, Utf8NoBom);
        var writer = new StreamWriter(pipe, Utf8NoBom) { AutoFlush = true };

        string? line;
        try
        {
            using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readDeadline.CancelAfter(_readTimeout);
            line = await reader.ReadLineAsync(readDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _log("A CLI stop connection sent no request and was dropped.");
            return;
        }

        var request = StopRequest.FromWire(line);

        // All replies go through one queue drained by a single writer task, so the handler's progress callback
        // never blocks (it can fire on the UI thread via Logger.LineForUi) and two writes can never interleave.
        var replies = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxQueuedLines) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        var pump = PumpAsync(writer, replies.Reader);

        if (request is null)
        {
            replies.Writer.TryWrite(LauncherIpc.ErrPrefix + "Unrecognized request.");
        }
        else
        {
            _log($"CLI stop request received: {request.ToWire()}");
            var outcome = await _handler(request, text => replies.Writer.TryWrite(LauncherIpc.LogPrefix + Flatten(text)))
                .ConfigureAwait(false);
            replies.Writer.TryWrite((outcome.Succeeded ? LauncherIpc.OkPrefix : LauncherIpc.ErrPrefix) + Flatten(outcome.Message));
        }

        replies.Writer.Complete();
        await pump.ConfigureAwait(false);
    }

    /// <summary>Drain queued reply lines to the client, one at a time. Bounded by <see cref="_writeTimeout"/> per
    /// line so a client that stopped reading costs one timeout, not the listener.</summary>
    private async Task PumpAsync(StreamWriter writer, ChannelReader<string> replies)
    {
        await foreach (var line in replies.ReadAllAsync().ConfigureAwait(false))
        {
            using var deadline = new CancellationTokenSource(_writeTimeout);
            try
            {
                await writer.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The client stopped reading or went away. Abandon the connection; the caller still completes the
                // stop, and the CLI reports the lost connection rather than a false success.
                return;
            }
        }
    }

    /// <summary>Collapse newlines: the protocol is line-framed, so an embedded one would look like a new reply.</summary>
    private static string Flatten(string text) =>
        text.ReplaceLineEndings(" ").Trim();

    /// <summary>
    /// Create the pipe with a DACL granting only the user running the launcher. Creating a DACL on an object you
    /// own needs no elevation (only a SACL or a foreign owner would). <paramref name="claimName"/> adds
    /// FirstPipeInstance for the very first create: \\.\pipe has no per-user namespace and the name is derivable
    /// from the install path, so without it another local user could pre-create the name and answer for us. It is
    /// off for later creates in the accept loop, where we are re-taking a name we already own.
    /// </summary>
    private static NamedPipeServerStream CreatePipe(string pipeName, bool claimName)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current user SID.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous;
        if (claimName)
            options |= PipeOptions.FirstPipeInstance;

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            security,
            HandleInheritability.None,
            additionalAccessRights: default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        // Give a request that is mid-reply a moment to land, so closing the launcher during a scripted stop
        // doesn't report failure for a stop that worked. Bounded: a longer stop is abandoned rather than holding
        // up shutdown, and the CLI then reports the lost connection.
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The loop's own failures are already logged.
        }
        _cts.Dispose();
    }
}

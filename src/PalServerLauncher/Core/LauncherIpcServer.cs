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

    /// <summary>Cap on queued progress lines. Reached routinely, not just by a stalled client: the handler
    /// enqueues far faster than the pump can write, so a burst of launcher log lines outruns even a healthy
    /// reader (measured: 40000 reported lines arrive as ~514). Progress is best effort, so overflow drops the
    /// oldest and the client keeps seeing the newest activity.</summary>
    private const int MaxQueuedLines = 512;

    /// <summary>Tries to re-take the pipe name between connections before giving up on CLI control.</summary>
    private const int RecreateAttempts = 10;
    private static readonly TimeSpan RecreateRetryDelay = TimeSpan.FromMilliseconds(100);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _pipeName;
    private readonly Func<StopRequest, Action<string>, Task<StopOutcome>> _handler;
    private readonly Action<string> _log;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _ownsPipeName;
    private bool _disposed;

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
                pipe = await CreatePipeAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log("Couldn't claim the CLI stop listener, another process holds the name. " +
                     "This launcher won't accept command-line stops.");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
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

        // PROGRESS ONLY goes through the queue, drained by a single writer task, so the handler's progress
        // callback never blocks (it can fire on the UI thread via Logger.LineForUi) and two writes can never
        // interleave. The terminal line is deliberately NOT queued: it is what the CLI's exit code is read from,
        // and a queue that drops under load must never be able to decide whether it is sent.
        var progress = Channel.CreateBounded<string>(
            new BoundedChannelOptions(MaxQueuedLines) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        var pump = PumpAsync(writer, progress.Reader);

        var terminal = LauncherIpc.ErrPrefix + "The launcher failed while handling the request.";
        try
        {
            if (request is null)
            {
                terminal = LauncherIpc.ErrPrefix + "Unrecognized request.";
            }
            else
            {
                _log($"CLI stop request received: {request.ToWire()}");
                var outcome = await _handler(request, text => progress.Writer.TryWrite(LauncherIpc.LogPrefix + Flatten(text)))
                    .ConfigureAwait(false);
                terminal = (outcome.Succeeded ? LauncherIpc.OkPrefix : LauncherIpc.ErrPrefix) + Flatten(outcome.Message);
            }
        }
        finally
        {
            // Runs even when the handler throws: the channel must be completed or the pump parks on it forever,
            // and the client gets a real error line instead of an unexplained EOF. The pump is awaited inside its
            // own try so that a pump failure can neither skip the terminal line nor displace the handler's
            // exception on its way out.
            progress.Writer.Complete();
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch
            {
                // Already means the client is unreachable; the terminal write below reports it properly.
            }
            await WriteTerminalAsync(writer, terminal).ConfigureAwait(false);
        }
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
                // The client stopped reading or went away. Abandon the connection. The caller still completes the
                // stop, and the CLI reports the lost connection rather than a false success.
                return;
            }
        }
    }

    /// <summary>Write the one line the CLI's exit code depends on. Best effort by the time we get here: the
    /// connection is ending either way, and a failure to deliver it is already the client's "closed before
    /// finishing" case, so swallowing keeps it from displacing a handler exception on its way out.</summary>
    private async Task WriteTerminalAsync(StreamWriter writer, string line)
    {
        using var deadline = new CancellationTokenSource(_writeTimeout);
        try
        {
            await writer.WriteLineAsync(line.AsMemory(), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    /// <summary>Collapse newlines: the protocol is line-framed, so an embedded one would look like a new reply.</summary>
    private static string Flatten(string text) =>
        text.ReplaceLineEndings(" ").Trim();

    /// <summary>
    /// Create the next pipe instance. The name is NOT held between connections (a single-instance pipe is gone
    /// once disposed), so a recreate can lose a race against anything else opening the name, including a client
    /// that still holds a handle to the instance we just closed. Retry briefly before concluding the name is
    /// really taken, otherwise a momentary collision would silently end CLI control for the session.
    /// </summary>
    private async Task<NamedPipeServerStream> CreatePipeAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // FirstPipeInstance only on the very first create. \\.\pipe has no per-user namespace and the
                // name is derivable from the install path, so without it another local process could pre-create
                // the name at startup and answer for us.
                var pipe = CreatePipe(_pipeName, claimName: !_ownsPipeName);
                _ownsPipeName = true;
                return pipe;
            }
            catch (IOException) when (attempt < RecreateAttempts && _ownsPipeName)
            {
                await Task.Delay(RecreateRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Create the pipe with a DACL granting only the user running the launcher. Creating a DACL on an object you
    /// own needs no elevation (only a SACL or a foreign owner would).
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
        if (_disposed)
            return;
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}

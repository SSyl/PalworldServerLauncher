using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// The launcher end of <see cref="LauncherIpc"/>: listens for a <c>--stop-server</c> from a second copy of the exe
/// and runs it through the handler (which routes to the normal stop path, so the relaunch latch is set).
///
/// One connection at a time, on purpose. Stops are rare and user-driven, and a serial loop means a long graceful
/// stop can't be raced by a second request. A client that connects and then says nothing is dropped after
/// <see cref="RequestReadTimeout"/> so it can't wedge the loop.
/// </summary>
public sealed class LauncherIpcServer : IDisposable
{
    /// <summary>How long a connected client gets to actually send its request line before it is dropped.</summary>
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(5);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly string _pipeName;
    private readonly Func<StopRequest, Action<string>, Task<StopOutcome>> _handler;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _writeGate = new();
    private Task? _loop;

    /// <param name="handler">Runs the request. Its second argument is a progress sink, safe to call from any thread.</param>
    public LauncherIpcServer(string pipeName, Func<StopRequest, Action<string>, Task<StopOutcome>> handler, Action<string> log)
    {
        _pipeName = pipeName;
        _handler = handler;
        _log = log;
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
                pipe = CreatePipe(_pipeName);
            }
            catch (IOException)
            {
                // Name already taken: another launcher on this same data root owns it. Two launchers on one data
                // root already conflict over the server itself, so don't fight for the pipe or retry-spam.
                _log("Another launcher is already listening for CLI stop commands. This one won't accept them.");
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
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
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

        using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readDeadline.CancelAfter(RequestReadTimeout);
        var line = await reader.ReadLineAsync(readDeadline.Token).ConfigureAwait(false);

        var request = StopRequest.FromWire(line);
        if (request is null)
        {
            await WriteAsync(writer, LauncherIpc.ErrPrefix + "Unrecognized request.", ct).ConfigureAwait(false);
            return;
        }

        _log($"CLI stop request received: {request.ToWire()}");
        var outcome = await _handler(request, text => Report(writer, text)).ConfigureAwait(false);
        var prefix = outcome.Succeeded ? LauncherIpc.OkPrefix : LauncherIpc.ErrPrefix;
        await WriteAsync(writer, prefix + Flatten(outcome.Message), ct).ConfigureAwait(false);
    }

    /// <summary>Stream one progress line to the client. Best effort: the client may have walked away, and losing
    /// progress must never fail the stop that's already under way.</summary>
    private void Report(StreamWriter writer, string text)
    {
        try
        {
            lock (_writeGate)
                writer.WriteLine(LauncherIpc.LogPrefix + Flatten(text));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private async Task WriteAsync(StreamWriter writer, string line, CancellationToken ct)
    {
        // Same gate as Report: a progress line landing mid-write would desync the client's line framing.
        Task write;
        lock (_writeGate)
            write = writer.WriteLineAsync(line.AsMemory(), ct);
        await write.ConfigureAwait(false);
    }

    /// <summary>Collapse newlines: the protocol is line-framed, so an embedded one would look like a new reply.</summary>
    private static string Flatten(string text) =>
        text.ReplaceLineEndings(" ").Trim();

    /// <summary>Create the pipe with a DACL granting only the user running the launcher. Creating a DACL on an
    /// object you own needs no elevation (only a SACL or a foreign owner would).</summary>
    private static NamedPipeServerStream CreatePipe(string pipeName)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Could not resolve the current user SID.");

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security,
            HandleInheritability.None,
            additionalAccessRights: default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}

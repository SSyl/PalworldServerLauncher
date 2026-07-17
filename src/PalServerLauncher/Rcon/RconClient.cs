using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Rcon;

/// <summary>
/// A minimal RCON client over TCP for the local Palworld server: connect + authenticate with the AdminPassword,
/// then send raw commands and read the text responses. RCON is deprecated by Palworld and slated for removal, so
/// this is deliberately isolated and best-effort. Every failure surfaces as a result or exception the console can
/// show, and nothing else in the launcher depends on it, so when Palworld drops RCON the connect simply fails.
/// Not unit-tested (it's live socket I/O); the wire codec it uses (<see cref="RconPacket"/>) is.
/// </summary>
public sealed class RconClient : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _password;
    private readonly TimeSpan _timeout;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private int _nextId = 1;
    private bool _disposed;

    public RconClient(string host, int port, string password, TimeSpan? timeout = null)
    {
        _host = host;
        _port = port;
        _password = password ?? string.Empty;
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>True once <see cref="ConnectAsync"/> has authenticated and the stream is live.</summary>
    public bool IsConnected => _stream is not null;

    /// <summary>Open the TCP connection and authenticate, returning a specific failure reason on any problem.</summary>
    public async Task<RconConnectResult> ConnectAsync(CancellationToken ct = default)
    {
        if (_disposed || _stream is not null)
            return _stream is not null ? RconConnectResult.Connected : RconConnectResult.Error;

        try
        {
            using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            opCts.CancelAfter(_timeout);

            var tcp = new TcpClient();
            _tcp = tcp; // assign before ConnectAsync so a failed connect is still torn down (no leaked socket)
            await tcp.ConnectAsync(_host, _port, opCts.Token).ConfigureAwait(false);
            _stream = tcp.GetStream();

            var authId = NextId();
            await SendAsync(RconPacket.Encode(authId, RconPacket.TypeAuth, _password), opCts.Token).ConfigureAwait(false);

            // The server sends an (often empty) RESPONSE_VALUE then an AUTH_RESPONSE (type 2). Read until that
            // AUTH_RESPONSE: id == -1 means the password was rejected, any other id means success.
            while (true)
            {
                var message = await ReadMessageAsync(opCts.Token).ConfigureAwait(false);
                if (message.Type != RconPacket.TypeAuthResponse)
                    continue;
                if (message.Id == -1)
                {
                    Teardown();
                    return RconConnectResult.AuthFailed;
                }
                return RconConnectResult.Connected;
            }
        }
        catch (OperationCanceledException)
        {
            Teardown();
            return RconConnectResult.Unreachable; // couldn't connect or finish the handshake within the timeout
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            Teardown();
            return RconConnectResult.Unreachable;
        }
        catch
        {
            Teardown();
            return RconConnectResult.Error;
        }
    }

    /// <summary>Send a command and return the text response. Throws on I/O failure (the caller guards).</summary>
    public async Task<string> ExecuteAsync(string command, CancellationToken ct = default)
    {
        if (_stream is null)
            throw new InvalidOperationException("RCON is not connected.");

        using var opCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        opCts.CancelAfter(_timeout);

        var id = NextId();
        await SendAsync(RconPacket.Encode(id, RconPacket.TypeExecCommand, command), opCts.Token).ConfigureAwait(false);

        // Palworld returns command output as a single RESPONSE_VALUE and doesn't do the Source multi-packet
        // reassembly trick (a known-good Palworld client sets sourceMultiPacketSupport:false). So read the first
        // RESPONSE_VALUE and return its body. Ids are deliberately not matched: Palworld doesn't reliably echo
        // the request id (that same client sets strictCommandPacketIdMatching:false).
        while (true)
        {
            var message = await ReadMessageAsync(opCts.Token).ConfigureAwait(false);
            if (message.Type == RconPacket.TypeResponseValue)
                return message.Body;
        }
    }

    private async Task SendAsync(byte[] bytes, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("RCON is not connected.");
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private async Task<RconMessage> ReadMessageAsync(CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("RCON is not connected.");

        var sizeBuffer = new byte[4];
        await stream.ReadExactlyAsync(sizeBuffer, ct).ConfigureAwait(false);
        var size = BinaryPrimitives.ReadInt32LittleEndian(sizeBuffer);
        if (size < RconPacket.MinBodyFramePrefix || size > 4 + 4 + RconPacket.MaxBodyBytes + 2)
            throw new IOException($"RCON packet size out of range ({size}).");

        var frame = new byte[size];
        await stream.ReadExactlyAsync(frame, ct).ConfigureAwait(false);
        return RconPacket.Decode(frame);
    }

    private int NextId()
    {
        // Keep ids positive so a real id can never collide with the -1 auth-failure sentinel.
        if (_nextId >= int.MaxValue)
            _nextId = 1;
        return _nextId++;
    }

    private void Teardown()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _tcp = null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Teardown();
    }
}

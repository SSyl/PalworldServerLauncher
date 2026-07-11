using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;

namespace PalServerLauncher.Core;

/// <summary>
/// Probes whether a port is reachable from the internet, one port at a time. Palworld's game port never
/// answers an arbitrary probe, so for the duration of a check we bind our OWN temporary listener on the
/// port (a UDP echo, or a TCP accept-and-drop) and have check-host.cc probe our public IP:port from
/// external nodes: if the probe reaches our listener and (for UDP) is echoed back, the port is reachable.
/// This is why the check is stopped-only - the ports must be free for us to bind.
///
/// A loopback self-test runs first: if our own listener can't even accept a 127.0.0.1 connection, the
/// problem is local (couldn't bind / firewall) and we report <see cref="PortReachability.BlockedLocally"/>
/// rather than a misleading "not reachable from the internet". Sockets and the live service are verified
/// by running, not unit tests; the report parsing and verdict mapping are the unit-tested parts.
/// </summary>
public sealed class PortChecker
{
    private static readonly byte[] UdpProbePayload = Encoding.ASCII.GetBytes("PALPORTCHECK");
    private static readonly TimeSpan PollDelay = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LoopbackTimeout = TimeSpan.FromSeconds(2);
    private const int PollAttempts = 12; // ~18s cap per port; an early success returns sooner

    private readonly CheckHostClient _checkHost;
    private readonly Logger _logger;

    public PortChecker(CheckHostClient checkHost, Logger logger)
    {
        _checkHost = checkHost;
        _logger = logger;
    }

    /// <summary>Check one port of the given public <paramref name="target"/> IP.</summary>
    public Task<PortReachability> CheckPortAsync(string target, PortCheckItem item, CancellationToken ct) =>
        item.Protocol == PortProtocol.Udp
            ? CheckUdpAsync(target, item, ct)
            : CheckTcpAsync(target, item, ct);

    private async Task<PortReachability> CheckUdpAsync(string target, PortCheckItem item, CancellationToken ct)
    {
        UdpClient listener;
        try
        {
            listener = new UdpClient(item.Port); // binds IPv4 Any:port
        }
        catch (SocketException ex)
        {
            _logger.Debug($"Port check: couldn't bind UDP {item.Port} ({ex.SocketErrorCode}).");
            return PortReachability.BlockedLocally;
        }

        using var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var echo = RunUdpEchoAsync(listener, listenerCts.Token);
        try
        {
            if (!await UdpLoopbackOkAsync(item.Port, ct).ConfigureAwait(false))
                return PortReachability.BlockedLocally;

            return await ProbeAsync("udp", target, item.Port, UdpProbePayload, ct).ConfigureAwait(false);
        }
        finally
        {
            listenerCts.Cancel();
            listener.Dispose();
            await Swallow(echo).ConfigureAwait(false);
        }
    }

    private async Task<PortReachability> CheckTcpAsync(string target, PortCheckItem item, CancellationToken ct)
    {
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Any, item.Port);
            listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.Debug($"Port check: couldn't bind TCP {item.Port} ({ex.SocketErrorCode}).");
            return PortReachability.BlockedLocally;
        }

        using var listenerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var accept = RunTcpAcceptAsync(listener, listenerCts.Token);
        try
        {
            if (!await TcpLoopbackOkAsync(item.Port, ct).ConfigureAwait(false))
                return PortReachability.BlockedLocally;

            return await ProbeAsync("tcp", target, item.Port, payload: null, ct).ConfigureAwait(false);
        }
        finally
        {
            listenerCts.Cancel();
            listener.Stop();
            await Swallow(accept).ConfigureAwait(false);
        }
    }

    /// <summary>Submit the probe and poll its report until every node reports (or the cap), mapping the
    /// aggregate to a reachability. One reachable node is enough to call the port open.</summary>
    private async Task<PortReachability> ProbeAsync(string method, string target, int port, byte[]? payload, CancellationToken ct)
    {
        var uuid = await _checkHost.SubmitAsync(method, target, port, payload, ct: ct).ConfigureAwait(false);
        if (uuid is null)
            return PortReachability.Inconclusive;

        for (var attempt = 0; attempt < PollAttempts; attempt++)
        {
            await Task.Delay(PollDelay, ct).ConfigureAwait(false);

            var nodes = await _checkHost.PollAsync(uuid, ct).ConfigureAwait(false);
            if (nodes is null)
                continue; // transient HTTP hiccup - keep polling

            var aggregate = CheckHostReport.Aggregate(nodes);
            if (aggregate.AnyReachable)
                return PortReachability.Reachable;
            if (aggregate.AllReported)
                return PortReachability.Unreachable;
        }
        return PortReachability.Inconclusive;
    }

    // --- listener loops (run for the duration of a single port's check) ---

    private static async Task RunUdpEchoAsync(UdpClient listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            try
            {
                await listener.SendAsync(received.Buffer, received.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                // Couldn't echo this one; keep listening for the real probe.
            }
        }
    }

    private static async Task RunTcpAcceptAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                // check-host only needs the connection to succeed; drop it immediately.
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    // --- loopback self-tests (127.0.0.1 is firewall-exempt, so this mainly proves the listener bound/works) ---

    private static async Task<bool> UdpLoopbackOkAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new UdpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoopbackTimeout);

            await probe.SendAsync(UdpProbePayload, new IPEndPoint(IPAddress.Loopback, port), timeout.Token).ConfigureAwait(false);
            var echo = await probe.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return echo.Buffer.Length > 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // our own loopback timeout, not a user cancel
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task<bool> TcpLoopbackOkAsync(int port, CancellationToken ct)
    {
        try
        {
            using var probe = new TcpClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LoopbackTimeout);

            await probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token).ConfigureAwait(false);
            return probe.Connected;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static async Task Swallow(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch { /* the listener loops end via cancellation; nothing to surface */ }
    }
}

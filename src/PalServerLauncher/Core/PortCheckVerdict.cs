namespace PalServerLauncher.Core;

/// <summary>Outcome of probing one port from the internet.</summary>
public enum PortReachability
{
    /// <summary>At least one external node reached the port.</summary>
    Reachable,
    /// <summary>Nodes reported back but none reached the port (closed or filtered).</summary>
    Unreachable,
    /// <summary>No node reported in time - can't say either way.</summary>
    Inconclusive,
    /// <summary>Our own listener couldn't even accept a loopback connection: a local firewall block, not the internet.</summary>
    BlockedLocally,
}

/// <summary>Severity shown for a port row - drives the status light colour.</summary>
public enum VerdictLevel { Ok, Warn, Fail, Unknown }

public sealed record PortVerdict(VerdictLevel Level, string Message);

/// <summary>
/// Pure mapping of (port role, reachability, service-up) -> a colour-coded verdict + message. Kept free of
/// sockets / HTTP / WPF so it is unit-testable, mirroring <see cref="ViewModels.PrimaryButton"/>. The
/// interpretation flips by role: a reachable game/query port is good (green), a reachable REST/RCON admin
/// port is a warning (amber). When the check-host service itself is down, every row is Unknown so a service
/// outage is never rendered as a closed port.
/// </summary>
public static class PortCheckVerdict
{
    public static PortVerdict Evaluate(PortKind kind, PortReachability reachability, bool serviceUp)
    {
        if (!serviceUp)
            return new PortVerdict(VerdictLevel.Unknown, "Port check service unavailable - can't test this port.");

        return reachability switch
        {
            PortReachability.BlockedLocally => new PortVerdict(VerdictLevel.Unknown,
                "Blocked on this PC - allow the launcher through Windows Firewall, then retry."),
            PortReachability.Inconclusive => new PortVerdict(VerdictLevel.Unknown,
                "Inconclusive - no probe nodes reported back. Try again."),
            PortReachability.Reachable => WhenReachable(kind),
            _ => WhenUnreachable(kind),
        };
    }

    private static PortVerdict WhenReachable(PortKind kind) => kind switch
    {
        PortKind.Game => new(VerdictLevel.Ok, "Reachable from the internet - players can connect."),
        PortKind.Query => new(VerdictLevel.Ok, "Reachable - the server can list in the community browser."),
        _ => new(VerdictLevel.Warn, "Reachable from the internet - this admin port should NOT be public-facing."),
    };

    private static PortVerdict WhenUnreachable(PortKind kind) => kind switch
    {
        PortKind.Game => new(VerdictLevel.Fail, "Not reachable - players can't connect. Check your port forward and firewall."),
        PortKind.Query => new(VerdictLevel.Warn, "Not reachable - only needed for the community server browser."),
        _ => new(VerdictLevel.Ok, "Not reachable from the internet - good, admin ports should stay private."),
    };
}

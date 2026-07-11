using System.Text.Json;

namespace PalServerLauncher.Rest;

/// <summary>Per-node outcome of a single check-host probe.</summary>
public enum NodeState
{
    /// <summary>The slave hasn't reported yet (the value is null while the probe is in flight).</summary>
    Pending,
    /// <summary>The target replied / the connection succeeded (status 1).</summary>
    Reachable,
    /// <summary>The probe finished without a reply (status 0: timeout, or a TCP reset / error).</summary>
    Unreachable,
}

public sealed record CheckHostNode(string Name, NodeState State, string? ErrorText);

/// <summary>Aggregated view across every probe node in one report.</summary>
public sealed record CheckHostAggregate(int Reachable, int Unreachable, int Pending)
{
    public int Total => Reachable + Unreachable + Pending;

    /// <summary>At least one node reached the target - the port is reachable from the internet.</summary>
    public bool AnyReachable => Reachable > 0;

    /// <summary>Every node that exists has reported (none still pending), and there is at least one node.</summary>
    public bool AllReported => Total > 0 && Pending == 0;
}

/// <summary>
/// Pure parsing of a check-host.cc report body (<c>GET /report/{uuid}</c>) into per-node outcomes.
/// The report's <c>data</c> is a node-name -> result map whose values are <c>null</c> while a probe
/// is in flight and become an object with a <c>checks</c> array once the slave reports. Deliberately
/// tolerant of missing / null / oddly-shaped entries (all treated as still-pending) so polling an
/// incomplete report never throws.
/// </summary>
public static class CheckHostReport
{
    public static IReadOnlyList<CheckHostNode> ParseNodes(string reportJson)
    {
        var nodes = new List<CheckHostNode>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(reportJson); }
        catch (JsonException) { return nodes; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return nodes;

            foreach (var node in data.EnumerateObject())
                nodes.Add(new CheckHostNode(node.Name, ReadState(node.Value, out var errorText), errorText));
        }
        return nodes;
    }

    public static CheckHostAggregate Aggregate(IEnumerable<CheckHostNode> nodes)
    {
        int reachable = 0, unreachable = 0, pending = 0;
        foreach (var node in nodes)
            switch (node.State)
            {
                case NodeState.Reachable: reachable++; break;
                case NodeState.Unreachable: unreachable++; break;
                default: pending++; break;
            }
        return new CheckHostAggregate(reachable, unreachable, pending);
    }

    public static CheckHostAggregate Parse(string reportJson) => Aggregate(ParseNodes(reportJson));

    private static NodeState ReadState(JsonElement node, out string? errorText)
    {
        errorText = null;

        // null while in flight, or any unexpected shape -> not reported yet.
        if (node.ValueKind != JsonValueKind.Object)
            return NodeState.Pending;
        if (!node.TryGetProperty("checks", out var checks) || checks.ValueKind != JsonValueKind.Array || checks.GetArrayLength() == 0)
            return NodeState.Pending;

        var check = checks[0];
        if (check.ValueKind != JsonValueKind.Object)
            return NodeState.Pending;

        if (check.TryGetProperty("errortext", out var et) && et.ValueKind == JsonValueKind.String)
            errorText = et.GetString();

        if (check.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number && st.TryGetInt32(out var status))
            return status == 1 ? NodeState.Reachable : NodeState.Unreachable;

        return NodeState.Pending;
    }
}

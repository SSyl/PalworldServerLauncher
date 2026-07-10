using PalServerLauncher.Config;

namespace PalServerLauncher.Core;

/// <summary>Transport used to probe a port - drives the check-host method and which listener we bind.</summary>
public enum PortProtocol { Udp, Tcp }

/// <summary>Which server port a row represents - drives how its result is interpreted.</summary>
public enum PortKind { Game, Query, Rest, Rcon }

/// <summary>One port to test: its role, label, protocol, and number (seeded from config/ini, user-editable).</summary>
public sealed record PortCheckItem(PortKind Kind, string Label, PortProtocol Protocol, int Port);

/// <summary>
/// Derives the default set of ports to test from the launcher config and the parsed game ini. Pure and
/// unit-tested. The query port has no persisted value (the launcher auto-picks a free one at launch via
/// <see cref="ServerController.FindFreeUdpPort"/>), so it defaults to Palworld's 27015 and the dialog notes
/// the check tests "the port you'll forward", not a live server port.
/// </summary>
public static class PortCheckPlan
{
    public const int DefaultQueryPort = 27015;

    public static IReadOnlyList<PortCheckItem> Build(LauncherConfig config, PalworldServerSettings settings) => new[]
    {
        new PortCheckItem(PortKind.Game, "Game port", PortProtocol.Udp, config.ServerPort),
        new PortCheckItem(PortKind.Query, "Steam/Query port", PortProtocol.Udp, DefaultQueryPort),
        new PortCheckItem(PortKind.Rest, "REST API port", PortProtocol.Tcp, settings.RestApiPortOrDefault),
        new PortCheckItem(PortKind.Rcon, "RCON port", PortProtocol.Tcp, settings.RconPortOrDefault),
    };
}

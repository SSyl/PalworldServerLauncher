using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class PortCheckVerdictTests
{
    [Theory]
    [InlineData(PortKind.Game)]
    [InlineData(PortKind.Query)]
    [InlineData(PortKind.Rest)]
    [InlineData(PortKind.Rcon)]
    public void Service_down_makes_every_row_unknown(PortKind kind)
    {
        // A check-host outage must never be rendered as a closed port.
        var v = PortCheckVerdict.Evaluate(kind, PortReachability.Reachable, serviceUp: false);
        Assert.Equal(VerdictLevel.Unknown, v.Level);
    }

    [Fact]
    public void Game_port_reachable_is_ok_unreachable_is_fail()
    {
        Assert.Equal(VerdictLevel.Ok, Evaluate(PortKind.Game, PortReachability.Reachable).Level);
        Assert.Equal(VerdictLevel.Fail, Evaluate(PortKind.Game, PortReachability.Unreachable).Level);
    }

    [Theory]
    [InlineData(PortKind.Rest)]
    [InlineData(PortKind.Rcon)]
    public void Admin_ports_reachable_is_warn_unreachable_is_ok(PortKind kind)
    {
        // The interpretation is inverted for admin ports: exposed = warn, private = good.
        Assert.Equal(VerdictLevel.Warn, Evaluate(kind, PortReachability.Reachable).Level);
        Assert.Equal(VerdictLevel.Ok, Evaluate(kind, PortReachability.Unreachable).Level);
    }

    [Fact]
    public void Query_port_reachable_is_ok_unreachable_is_warn()
    {
        Assert.Equal(VerdictLevel.Ok, Evaluate(PortKind.Query, PortReachability.Reachable).Level);
        Assert.Equal(VerdictLevel.Warn, Evaluate(PortKind.Query, PortReachability.Unreachable).Level);
    }

    [Theory]
    [InlineData(PortReachability.BlockedLocally)]
    [InlineData(PortReachability.Inconclusive)]
    public void Local_block_and_inconclusive_are_unknown(PortReachability reachability)
    {
        Assert.Equal(VerdictLevel.Unknown, Evaluate(PortKind.Game, reachability).Level);
    }

    [Fact]
    public void BlockedLocally_message_points_at_the_firewall()
    {
        var v = Evaluate(PortKind.Game, PortReachability.BlockedLocally);
        Assert.Contains("Firewall", v.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_derives_ports_from_config_and_ini()
    {
        var config = new LauncherConfig { ServerPort = 7777 };
        var settings = IniReader.Parse("""OptionSettings=(RESTAPIPort=8212,RCONPort=25575)""");

        var plan = PortCheckPlan.Build(config, settings);

        Assert.Equal(7777, plan.Single(p => p.Kind == PortKind.Game).Port);
        Assert.Equal(PortCheckPlan.DefaultQueryPort, plan.Single(p => p.Kind == PortKind.Query).Port);
        Assert.Equal(8212, plan.Single(p => p.Kind == PortKind.Rest).Port);
        Assert.Equal(25575, plan.Single(p => p.Kind == PortKind.Rcon).Port);
        Assert.Equal(PortProtocol.Udp, plan.Single(p => p.Kind == PortKind.Game).Protocol);
        Assert.Equal(PortProtocol.Tcp, plan.Single(p => p.Kind == PortKind.Rest).Protocol);
    }

    [Fact]
    public void Build_falls_back_to_defaults_when_ini_is_empty()
    {
        var plan = PortCheckPlan.Build(new LauncherConfig(), IniReader.Parse("nothing here"));

        Assert.Equal(8211, plan.Single(p => p.Kind == PortKind.Game).Port);   // LauncherConfig default
        Assert.Equal(8212, plan.Single(p => p.Kind == PortKind.Rest).Port);   // RestApiPortOrDefault
        Assert.Equal(25575, plan.Single(p => p.Kind == PortKind.Rcon).Port);  // RconPortOrDefault
    }

    [Fact]
    public void Build_uses_the_configured_query_port_when_set()
    {
        // With an explicit query port the check tests THAT port, not the 27015 auto default.
        var plan = PortCheckPlan.Build(new LauncherConfig { QueryPort = 27020 }, IniReader.Parse("nothing here"));

        Assert.Equal(27020, plan.Single(p => p.Kind == PortKind.Query).Port);
    }

    private static PortVerdict Evaluate(PortKind kind, PortReachability reachability) =>
        PortCheckVerdict.Evaluate(kind, reachability, serviceUp: true);
}

using System.Linq;
using PalServerLauncher.Rest;

namespace PalServerLauncher.Tests;

public class CheckHostReportTests
{
    [Fact]
    public void Complete_report_aggregates_reachable_and_unreachable()
    {
        // Two nodes replied (status 1), one timed out (status 0) - the port is reachable overall.
        var json = """
            {"success":true,"data":{
              "US-DAL":{"checks":[{"status":1,"errortext":"","connectiontime":37}]},
              "US-LOS":{"checks":[{"status":1,"errortext":"","connectiontime":2}]},
              "US-MIA":{"checks":[{"status":0,"errortext":"Timeout","connectiontime":0}]}
            }}
            """;

        var agg = CheckHostReport.Parse(json);

        Assert.Equal(2, agg.Reachable);
        Assert.Equal(1, agg.Unreachable);
        Assert.Equal(0, agg.Pending);
        Assert.True(agg.AnyReachable);
        Assert.True(agg.AllReported);
    }

    [Fact]
    public void In_flight_null_nodes_are_pending_not_a_crash()
    {
        // While a probe is in flight, un-reported nodes come back as null.
        var json = """
            {"success":true,"data":{
              "US-DAL":{"checks":[{"status":1,"errortext":""}]},
              "US-LOS":null
            }}
            """;

        var agg = CheckHostReport.Parse(json);

        Assert.Equal(1, agg.Reachable);
        Assert.Equal(1, agg.Pending);
        Assert.False(agg.AllReported); // still waiting on US-LOS
    }

    [Fact]
    public void Node_object_without_checks_is_pending()
    {
        var agg = CheckHostReport.Parse("""{"success":true,"data":{"US-DAL":{}}}""");

        Assert.Equal(1, agg.Pending);
        Assert.False(agg.AllReported);
    }

    [Fact]
    public void Empty_checks_array_and_null_check_entry_are_pending()
    {
        var agg = CheckHostReport.Parse("""
            {"data":{"US-A":{"checks":[]},"US-B":{"checks":[null]}}}
            """);

        Assert.Equal(2, agg.Pending);
        Assert.Equal(0, agg.Reachable);
    }

    [Fact]
    public void Tcp_closed_reports_unreachable_with_errortext()
    {
        var nodes = CheckHostReport.ParseNodes("""
            {"data":{"US-DAL":{"checks":[{"status":0,"errortext":"ERROR","connectiontime":0}]}}}
            """);

        var node = Assert.Single(nodes);
        Assert.Equal(NodeState.Unreachable, node.State);
        Assert.Equal("ERROR", node.ErrorText);
    }

    [Fact]
    public void Missing_data_or_garbage_yields_no_nodes()
    {
        Assert.Empty(CheckHostReport.ParseNodes("""{"success":true}"""));
        Assert.Empty(CheckHostReport.ParseNodes("not json at all"));
        Assert.Empty(CheckHostReport.ParseNodes(""));
    }

    [Fact]
    public void Aggregate_of_no_nodes_is_not_all_reported()
    {
        var agg = CheckHostReport.Parse("""{"data":{}}""");

        Assert.Equal(0, agg.Total);
        Assert.False(agg.AllReported); // no nodes at all is "inconclusive", not "done"
        Assert.False(agg.AnyReachable);
    }
}

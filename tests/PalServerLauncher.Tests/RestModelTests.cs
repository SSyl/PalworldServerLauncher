using System.Text.Json;
using PalServerLauncher.Rest;
using PalServerLauncher.Rest.Models;

namespace PalServerLauncher.Tests;

public class RestModelTests
{
    private static T Deser<T>(string json) => JsonSerializer.Deserialize<T>(json, PalworldRestClient.JsonOptions)!;

    [Fact]
    public void Metrics_maps_all_fields()
    {
        // Includes an unknown field to prove forward-compat (1.0 may add fields).
        var json = """
            {"serverfps":57,"currentplayernum":10,"serverframetime":16.7671,"maxplayernum":32,"uptime":3600,"basecampnum":32,"days":1,"futurefield":"ignored"}
            """;

        var m = Deser<MetricsResponse>(json);

        Assert.Equal(57, m.ServerFps);
        Assert.Equal(10, m.CurrentPlayerNum);
        Assert.Equal(16.7671, m.ServerFrameTime, 4);
        Assert.Equal(32, m.MaxPlayerNum);
        Assert.Equal(3600L, m.Uptime);
        Assert.Equal(1, m.Days);
        Assert.Equal(32, m.BaseCampNum);
    }

    [Fact]
    public void Info_maps_fields()
    {
        var info = Deser<InfoResponse>("""{"version":"v1.0.0.12345","servername":"Syl's Pals","description":"hi"}""");

        Assert.Equal("v1.0.0.12345", info.Version);
        Assert.Equal("Syl's Pals", info.ServerName);
        Assert.Equal("hi", info.Description);
        Assert.Null(info.WorldGuid); // absent -> null, no throw
    }

    [Fact]
    public void Players_maps_list_and_snake_case_fields()
    {
        var json = """
            {"players":[{"name":"Alice","accountName":"steam_1","playerId":"pid1","userId":"uid1","ip":"1.2.3.4","ping":42.5,"location_x":100.0,"location_y":200.0,"level":15,"building_count":7}]}
            """;

        var resp = Deser<PlayersResponse>(json);

        Assert.Single(resp.Players);
        var p = resp.Players[0];
        Assert.Equal("Alice", p.Name);
        Assert.Equal("pid1", p.PlayerId);
        Assert.Equal(42.5, p.Ping, 3);
        Assert.Equal(100.0, p.LocationX, 3);
        Assert.Equal(15, p.Level);
        Assert.Equal(7, p.BuildingCount);
    }

    [Fact]
    public void Empty_players_list_deserializes()
    {
        var resp = Deser<PlayersResponse>("""{"players":[]}""");
        Assert.Empty(resp.Players);
    }
}

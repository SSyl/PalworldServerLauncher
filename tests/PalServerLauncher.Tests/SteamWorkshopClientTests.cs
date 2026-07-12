using PalServerLauncher.Rest;

namespace PalServerLauncher.Tests;

public class SteamWorkshopClientTests
{
    [Fact]
    public void Parses_a_valid_details_response()
    {
        // The real shape returned by GetPublishedFileDetails for workshop id 3625223587.
        var json = """{"response":{"result":1,"resultcount":1,"publishedfiledetails":[{"publishedfileid":"3625223587","result":1,"title":"UE4SS Experimental (Palworld)","consumer_app_id":1623730,"time_updated":1783643392}]}}""";
        var d = SteamWorkshopClient.ParseDetails(json);
        Assert.NotNull(d);
        Assert.Equal("3625223587", d!.Id);
        Assert.Equal("UE4SS Experimental (Palworld)", d.Title);
        Assert.Equal(1623730, d.ConsumerAppId);
        Assert.Equal(1783643392L, d.TimeUpdated);
    }

    [Fact]
    public void Returns_null_for_a_missing_item()
    {
        // Per-item result 9 = item not found.
        var json = """{"response":{"result":1,"publishedfiledetails":[{"publishedfileid":"1","result":9}]}}""";
        Assert.Null(SteamWorkshopClient.ParseDetails(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("""{"response":{}}""")]
    [InlineData("""{"response":{"publishedfiledetails":[]}}""")]
    public void Returns_null_on_bad_shape(string json) =>
        Assert.Null(SteamWorkshopClient.ParseDetails(json));
}

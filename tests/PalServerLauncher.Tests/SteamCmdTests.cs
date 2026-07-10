using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class SteamCmdTests
{
    [Fact]
    public void ParseBuildId_reads_appmanifest_acf()
    {
        var acf = """
            "AppState"
            {
            	"appid"		"2394010"
            	"Universe"		"1"
            	"name"		"Palworld Dedicated Server"
            	"StateFlags"		"4"
            	"buildid"		"17754784"
            	"LastUpdated"		"1719000000"
            }
            """;

        Assert.Equal("17754784", SteamCmd.ParseBuildId(acf));
    }

    [Fact]
    public void ParseBuildId_reads_first_buildid_from_app_info_public_branch()
    {
        var appInfo = """
            "2394010"
            {
            	"depots"
            	{
            		"branches"
            		{
            			"public"
            			{
            				"buildid"		"18010101"
            				"timeupdated"		"1720000000"
            			}
            			"beta"
            			{
            				"buildid"		"18099999"
            			}
            		}
            	}
            }
            """;

        Assert.Equal("18010101", SteamCmd.ParseBuildId(appInfo));
    }

    [Fact]
    public void ParseBuildId_returns_null_when_absent()
    {
        Assert.Null(SteamCmd.ParseBuildId("no build id here"));
    }

    [Fact]
    public void ReadInstalledBuildId_null_when_not_installed()
    {
        var steam = new SteamCmd(@"Z:\no\such\root");
        Assert.Null(steam.ReadInstalledBuildId());
    }
}

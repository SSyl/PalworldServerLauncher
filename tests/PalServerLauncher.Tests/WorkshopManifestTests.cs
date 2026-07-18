using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class WorkshopManifestTests
{
    // A real appworkshop_1623730.acf captured from a live SteamCMD Workshop cache (two Palworld mods installed).
    private const string RealAcf = """
        "AppWorkshop"
        {
        	"appid"		"1623730"
        	"SizeOnDisk"		"17264961"
        	"NeedsUpdate"		"0"
        	"NeedsDownload"		"0"
        	"TimeLastUpdated"		"0"
        	"TimeLastAppRan"		"0"
        	"LastBuildID"		"0"
        	"WorkshopItemsInstalled"
        	{
        		"3625223587"
        		{
        			"size"		"17037355"
        			"timeupdated"		"1783976460"
        			"manifest"		"4657833342736456707"
        		}
        		"3765995942"
        		{
        			"size"		"227606"
        			"timeupdated"		"1784334134"
        			"manifest"		"4745656784016778579"
        		}
        	}
        	"WorkshopItemDetails"
        	{
        		"3625223587"
        		{
        			"manifest"		"4657833342736456707"
        			"timeupdated"		"1783976460"
        			"timetouched"		"1784403552"
        			"latest_timeupdated"		"1783976460"
        			"latest_manifest"		"4657833342736456707"
        		}
        	}
        }
        """;

    [Fact]
    public void Parses_installed_items_from_real_acf()
    {
        var items = WorkshopManifest.ParseInstalled(RealAcf);

        Assert.Equal(2, items.Count);
        Assert.Equal("4657833342736456707", items["3625223587"].Manifest);
        Assert.Equal(1783976460L, items["3625223587"].TimeUpdated);
        Assert.Equal("4745656784016778579", items["3765995942"].Manifest);
        Assert.Equal(1784334134L, items["3765995942"].TimeUpdated);
    }

    [Fact]
    public void Ignores_the_workshop_item_details_section()
    {
        // manifest/timeupdated appear under both sections; only WorkshopItemsInstalled feeds the gate.
        var items = WorkshopManifest.ParseInstalled(RealAcf);
        Assert.DoesNotContain(items.Values, s => s.Manifest.Length == 0);
    }

    [Fact]
    public void Empty_when_section_absent()
    {
        var acf = """
            "AppWorkshop"
            {
            	"appid"		"1623730"
            }
            """;
        Assert.Empty(WorkshopManifest.ParseInstalled(acf));
    }

    [Fact]
    public void Empty_when_no_items_installed()
    {
        var acf = """
            "AppWorkshop"
            {
            	"WorkshopItemsInstalled"
            	{
            	}
            }
            """;
        Assert.Empty(WorkshopManifest.ParseInstalled(acf));
    }

    [Fact]
    public void Skips_an_item_missing_its_manifest()
    {
        var acf = """
            "AppWorkshop"
            {
            	"WorkshopItemsInstalled"
            	{
            		"111"
            		{
            			"size"		"10"
            			"timeupdated"		"5"
            		}
            		"222"
            		{
            			"manifest"		"999"
            		}
            	}
            }
            """;
        var items = WorkshopManifest.ParseInstalled(acf);
        Assert.Single(items);
        Assert.Equal("999", items["222"].Manifest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not valid kv")]
    [InlineData("{}{}{}")]
    public void Empty_on_junk_input(string acf) =>
        Assert.Empty(WorkshopManifest.ParseInstalled(acf));
}

using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class PalModSettingsFileTests
{
    // The exact file the standard 1.0 server generates on first launch.
    private const string Generated =
        "[PalModSettings]\nbGlobalEnableMod=False\nWorkshopRootDir=\nConfigVersion=1.0\n";

    [Fact]
    public void Round_trip_with_no_edits_reproduces_the_generated_file()
    {
        var file = PalModSettingsFile.Load(Generated);
        Assert.False(file.GlobalEnable);
        Assert.Empty(file.ActiveMods);
        Assert.Equal(Generated, file.Render());
    }

    [Fact]
    public void Enabling_mods_sets_the_flag_and_list_and_keeps_other_keys()
    {
        var file = PalModSettingsFile.Load(Generated);
        file.SetGlobalEnable(true);
        file.SetActiveMods(new[] { "GamingCattiva", "FarmingQuivern" });
        var rendered = file.Render();

        Assert.Contains("bGlobalEnableMod=True", rendered);
        Assert.Contains("ActiveModList=GamingCattiva", rendered);
        Assert.Contains("ActiveModList=FarmingQuivern", rendered);
        Assert.Contains("WorkshopRootDir=", rendered);   // preserved
        Assert.Contains("ConfigVersion=1.0", rendered);  // preserved

        var reloaded = PalModSettingsFile.Load(rendered);
        Assert.True(reloaded.GlobalEnable);
        Assert.Equal(new[] { "GamingCattiva", "FarmingQuivern" }, reloaded.ActiveMods);
    }

    [Fact]
    public void Existing_active_list_is_replaced_not_duplicated()
    {
        var withMods = "[PalModSettings]\nbGlobalEnableMod=True\nActiveModList=Old\nConfigVersion=1.0\n";
        var file = PalModSettingsFile.Load(withMods);
        Assert.Equal(new[] { "Old" }, file.ActiveMods);

        file.SetActiveMods(new[] { "New" });
        var rendered = file.Render();
        Assert.Contains("ActiveModList=New", rendered);
        Assert.DoesNotContain("ActiveModList=Old", rendered);
        Assert.Single(PalModSettingsFile.Load(rendered).ActiveMods);
    }

    [Fact]
    public void Deduplicates_and_drops_blanks_from_the_active_list()
    {
        var file = PalModSettingsFile.Load(Generated);
        file.SetActiveMods(new[] { "A", "A", "", "  ", "B" });
        Assert.Equal(new[] { "A", "B" }, file.ActiveMods);
    }

    [Fact]
    public void Preserves_crlf_line_endings()
    {
        var crlf = "[PalModSettings]\r\nbGlobalEnableMod=False\r\nConfigVersion=1.0\r\n";
        Assert.Equal(crlf, PalModSettingsFile.Load(crlf).Render());
    }

    [Fact]
    public void Missing_section_is_created()
    {
        var file = PalModSettingsFile.Load("");
        file.SetGlobalEnable(true);
        file.SetActiveMods(new[] { "Foo" });
        var rendered = file.Render();
        Assert.Contains("[PalModSettings]", rendered);
        Assert.Contains("bGlobalEnableMod=True", rendered);
        Assert.Contains("ActiveModList=Foo", rendered);
    }
}

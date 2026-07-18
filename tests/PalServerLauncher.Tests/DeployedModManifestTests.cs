using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class DeployedModManifestTests
{
    // A realistic manifest for a forced Lua mod: its own deployed Lua dir + its ManagedMods record. The shared
    // UE4SS helper path and the UE4SS Mods root are the traps a naive "delete everything listed" would fall into.
    private const string LuaModManifest = """
        {
            "Files": [
                "Mods/NativeMods/UE4SS/Mods/SmartTransport/Scripts/main.lua",
                "Mods/NativeMods/UE4SS/Mods/shared/UEHelpers/UEHelpers.lua",
                "Mods/ManagedMods/SmartTransport/Info.json"
            ],
            "Dirs": [
                "Mods/NativeMods/UE4SS",
                "Mods/NativeMods/UE4SS/Mods",
                "Mods/NativeMods/UE4SS/Mods/SmartTransport",
                "Mods/NativeMods/UE4SS/Mods/SmartTransport/Scripts",
                "Mods/ManagedMods/SmartTransport"
            ],
            "WorkshopId": 3765995942
        }
        """;

    [Fact]
    public void Selects_only_paths_carrying_the_package_name()
    {
        var plan = DeployedModManifest.Select(LuaModManifest, "SmartTransport");

        Assert.Equal(new[]
        {
            "Mods/NativeMods/UE4SS/Mods/SmartTransport/Scripts/main.lua",
            "Mods/ManagedMods/SmartTransport/Info.json",
        }, plan.Files);

        Assert.Equal(new[]
        {
            "Mods/NativeMods/UE4SS/Mods/SmartTransport",
            "Mods/NativeMods/UE4SS/Mods/SmartTransport/Scripts",
            "Mods/ManagedMods/SmartTransport",
        }, plan.Dirs);
    }

    [Fact]
    public void Never_selects_ue4ss_core_or_shared_paths()
    {
        var plan = DeployedModManifest.Select(LuaModManifest, "SmartTransport");
        Assert.DoesNotContain("Mods/NativeMods/UE4SS", plan.Dirs);
        Assert.DoesNotContain("Mods/NativeMods/UE4SS/Mods", plan.Dirs);
        Assert.DoesNotContain(plan.Files, f => f.Contains("shared"));
    }

    [Theory]
    [InlineData("Mods/NativeMods/UE4SS/Mods/SmartTransport/Scripts", "SmartTransport", true)]
    [InlineData("Mods/ManagedMods/SmartTransport", "SmartTransport", true)]
    [InlineData("Mods/NativeMods/UE4SS/Mods/shared", "SmartTransport", false)]
    [InlineData("Mods/NativeMods/UE4SS", "SmartTransport", false)]
    [InlineData("Mods/NativeMods/UE4SS/Mods/SmartTransportExtra", "SmartTransport", false)] // substring, not a segment
    public void HasSegment_matches_full_segments_only(string path, string pkg, bool expected) =>
        Assert.Equal(expected, DeployedModManifest.HasSegment(path, pkg));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"Files":"nope","Dirs":123}""")]
    public void Empty_plan_on_bad_or_missing_data(string json)
    {
        var plan = DeployedModManifest.Select(json, "SmartTransport");
        Assert.Empty(plan.Files);
        Assert.Empty(plan.Dirs);
    }

    [Fact]
    public void Empty_plan_when_package_name_blank()
    {
        var plan = DeployedModManifest.Select(LuaModManifest, "");
        Assert.Empty(plan.Files);
        Assert.Empty(plan.Dirs);
    }
}

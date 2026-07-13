using System.Linq;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class LoosePakModsTests
{
    [Fact]
    public void Groups_an_iostore_trio_into_one_enabled_mod()
    {
        var mods = LoosePakMods.Scan(new[] { "CoolMod.pak", "CoolMod.utoc", "CoolMod.ucas" });

        var mod = Assert.Single(mods);
        Assert.Equal("CoolMod", mod.BaseName);
        Assert.True(mod.Enabled);
        Assert.Equal(3, mod.Files.Count);
    }

    [Fact]
    public void A_lone_pak_is_one_mod()
    {
        var mod = Assert.Single(LoosePakMods.Scan(new[] { "Simple.pak" }));
        Assert.Equal("Simple", mod.BaseName);
        Assert.True(mod.Enabled);
    }

    [Fact]
    public void All_files_disabled_reads_as_disabled()
    {
        var mod = Assert.Single(LoosePakMods.Scan(new[]
        {
            "CoolMod.pak.disabled", "CoolMod.utoc.disabled", "CoolMod.ucas.disabled",
        }));

        Assert.Equal("CoolMod", mod.BaseName);
        Assert.False(mod.Enabled);
        Assert.Equal(3, mod.Files.Count);
    }

    [Fact]
    public void Ignores_non_pak_files()
    {
        var mods = LoosePakMods.Scan(new[] { "readme.txt", "Mod.pak", "notes.md", "image.png" });
        Assert.Single(mods);
        Assert.Equal("Mod", mods[0].BaseName);
    }

    [Fact]
    public void Separate_base_names_are_separate_mods_sorted()
    {
        var mods = LoosePakMods.Scan(new[] { "Zebra.pak", "Alpha.pak", "Alpha.utoc" });

        Assert.Equal(2, mods.Count);
        Assert.Equal("Alpha", mods[0].BaseName);
        Assert.Equal("Zebra", mods[1].BaseName);
    }

    [Fact]
    public void Toggle_disable_appends_suffix_to_every_file()
    {
        var mod = Assert.Single(LoosePakMods.Scan(new[] { "CoolMod.pak", "CoolMod.utoc", "CoolMod.ucas" }));

        var plan = LoosePakMods.TogglePlan(mod, enable: false);

        Assert.Equal(3, plan.Count);
        Assert.All(plan, p => Assert.EndsWith(".disabled", p.To));
        Assert.Contains(plan, p => p is { From: "CoolMod.pak", To: "CoolMod.pak.disabled" });
    }

    [Fact]
    public void Toggle_enable_strips_the_suffix()
    {
        var mod = Assert.Single(LoosePakMods.Scan(new[] { "CoolMod.pak.disabled", "CoolMod.utoc.disabled" }));

        var plan = LoosePakMods.TogglePlan(mod, enable: true);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, p => p is { From: "CoolMod.pak.disabled", To: "CoolMod.pak" });
        Assert.DoesNotContain(plan, p => p.To.EndsWith(".disabled"));
    }

    [Fact]
    public void Toggle_returns_only_files_that_change()
    {
        // Already-disabled files shouldn't be re-renamed when disabling again.
        var mod = new LoosePakMods.LoosePakMod("CoolMod", Enabled: true, new[] { "CoolMod.pak", "CoolMod.utoc.disabled" });

        var plan = LoosePakMods.TogglePlan(mod, enable: false);

        var only = Assert.Single(plan);
        Assert.Equal("CoolMod.pak", only.From);
        Assert.Equal("CoolMod.pak.disabled", only.To);
    }

    [Theory]
    [InlineData("Mod.pak", true)]
    [InlineData("Mod.utoc", true)]
    [InlineData("Mod.ucas", true)]
    [InlineData("Mod.pak.disabled", true)]
    [InlineData("Mod.txt", false)]
    [InlineData("Mod", false)]
    public void IsPakFamily_recognizes_pak_files(string name, bool expected) =>
        Assert.Equal(expected, LoosePakMods.IsPakFamily(name));
}

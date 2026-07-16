using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class WorldOptionSavTests
{
    private const string Sav = @"C:\saves\x\y\WorldOption.sav";

    [Fact]
    public void BakTargetPath_uses_plain_bak_when_free()
    {
        Assert.Equal(Sav + ".bak", WorldOptionSav.BakTargetPath(Sav, _ => false));
    }

    [Fact]
    public void BakTargetPath_numbers_when_bak_is_taken()
    {
        var taken = new HashSet<string> { Sav + ".bak" };
        Assert.Equal(Sav + ".bak.1", WorldOptionSav.BakTargetPath(Sav, taken.Contains));
    }

    [Fact]
    public void BakTargetPath_increments_past_several_existing_baks()
    {
        var taken = new HashSet<string> { Sav + ".bak", Sav + ".bak.1", Sav + ".bak.2" };
        Assert.Equal(Sav + ".bak.3", WorldOptionSav.BakTargetPath(Sav, taken.Contains));
    }

    [Fact]
    public void BakTargetPath_returns_the_first_free_number_in_order()
    {
        // .bak and .bak.1 taken, .bak.2 free -> .bak.2 (first free by ascending count, never clobbers a backup).
        var taken = new HashSet<string> { Sav + ".bak", Sav + ".bak.1" };
        Assert.Equal(Sav + ".bak.2", WorldOptionSav.BakTargetPath(Sav, taken.Contains));
    }
}

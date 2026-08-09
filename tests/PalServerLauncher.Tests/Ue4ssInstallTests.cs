using PalServerLauncher.Core;
using Xunit;

namespace PalServerLauncher.Tests;

public class Ue4ssInstallTests
{
    [Theory]
    [InlineData(false, false, false, Ue4ssInstall.None)]
    [InlineData(true, false, false, Ue4ssInstall.Workshop)]
    [InlineData(false, true, true, Ue4ssInstall.Custom)]
    [InlineData(true, true, true, Ue4ssInstall.Both)]
    public void Classify_maps_each_combination_of_install_locations(bool workshop, bool custom, bool loads, Ue4ssInstall expected) =>
        Assert.Equal(expected, ModService.Classify(workshop, custom, loads));

    [Fact]
    public void A_hand_install_with_its_proxy_dll_renamed_away_is_not_a_conflict() =>
        Assert.Equal(Ue4ssInstall.Workshop, ModService.Classify(workshopPresent: true, customPresent: true, customLoads: false));

    [Fact]
    public void A_hand_install_still_counts_as_custom_with_its_proxy_dll_renamed_away() =>
        Assert.Equal(Ue4ssInstall.Custom, ModService.Classify(workshopPresent: false, customPresent: true, customLoads: false));
}

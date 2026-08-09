using PalServerLauncher.Core;
using Xunit;

namespace PalServerLauncher.Tests;

public class Ue4ssInstallTests
{
    [Theory]
    [InlineData(false, false, Ue4ssInstall.None)]
    [InlineData(true, false, Ue4ssInstall.Workshop)]
    [InlineData(false, true, Ue4ssInstall.Custom)]
    [InlineData(true, true, Ue4ssInstall.Both)]
    public void Classify_maps_each_combination_of_install_locations(bool workshop, bool custom, Ue4ssInstall expected) =>
        Assert.Equal(expected, ModService.Classify(workshop, custom));
}

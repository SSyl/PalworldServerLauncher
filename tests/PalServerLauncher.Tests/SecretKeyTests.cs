using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class SecretKeyTests
{
    [Theory]
    [InlineData("AdminPassword")]
    [InlineData("ServerPassword")]
    // Case-insensitive: the ini and the Undocumented tab both accept whatever casing the user types.
    [InlineData("adminpassword")]
    [InlineData("SERVERPASSWORD")]
    // Uncatalogued keys are reachable through the Undocumented tab, so the name has to carry the check.
    [InlineData("SomeFuturePassword")]
    [InlineData("rcon_password")]
    public void Secret_keys_are_recognized(string key) =>
        Assert.True(GameSettingsCatalog.IsSecretKey(key));

    [Theory]
    [InlineData("ServerName")]
    [InlineData("PublicIP")]
    [InlineData("RESTAPIEnabled")]
    [InlineData("bIsPvP")]
    [InlineData("BanListURL")]
    public void Ordinary_keys_are_not_masked(string key) =>
        Assert.False(GameSettingsCatalog.IsSecretKey(key));

    [Fact]
    public void Every_catalog_setting_flagged_Secret_is_covered()
    {
        foreach (var setting in GameSettingsCatalog.All.Where(s => s.Secret))
            Assert.True(GameSettingsCatalog.IsSecretKey(setting.Key), $"{setting.Key} is Secret but would be logged");
    }

    [Fact]
    public void The_mask_does_not_reveal_length() =>
        Assert.Equal("********", GameSettingsCatalog.SecretMask);
}

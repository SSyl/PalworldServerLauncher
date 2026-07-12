using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ModInfoTests
{
    [Fact]
    public void Parses_package_name_version_and_server_flag()
    {
        var json = """{"PackageName":"GamingCattiva","Version":"1.2.0","InstallRule":{"IsServer":true,"IsClient":true}}""";
        var info = ModInfo.Parse(json);
        Assert.NotNull(info);
        Assert.Equal("GamingCattiva", info!.PackageName);
        Assert.Equal("1.2.0", info.Version);
        Assert.True(info.IsServer);
    }

    [Fact]
    public void Server_flag_is_false_when_missing_or_not_true()
    {
        Assert.False(ModInfo.Parse("""{"PackageName":"X","InstallRule":{"IsClient":true}}""")!.IsServer);
        Assert.False(ModInfo.Parse("""{"PackageName":"X","InstallRule":{"IsServer":false}}""")!.IsServer);
        Assert.False(ModInfo.Parse("""{"PackageName":"X"}""")!.IsServer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]                    // no PackageName
    [InlineData("""{"Version":"1"}""")]   // no PackageName
    public void Returns_null_on_bad_input(string json) =>
        Assert.Null(ModInfo.Parse(json));
}

using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ModInfoTests
{
    [Fact]
    public void Parses_package_name_version_and_server_flag_from_array_rules()
    {
        // The real shape: InstallRule is an array of rule objects.
        var json = """{"PackageName":"UE4SSExperimentalPW","Version":"experimental-palworld-5","InstallRule":[{"Type":"UE4SS","Targets":["."]},{"Type":"UE4SS","IsServer":true,"Targets":["."]}]}""";
        var info = ModInfo.Parse(json);
        Assert.NotNull(info);
        Assert.Equal("UE4SSExperimentalPW", info!.PackageName);
        Assert.Equal("experimental-palworld-5", info.Version);
        Assert.True(info.IsServer);
    }

    [Fact]
    public void Server_flag_true_for_single_array_rule()
    {
        Assert.True(ModInfo.Parse("""{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":true}]}""")!.IsServer);
    }

    [Fact]
    public void Server_flag_false_when_no_rule_declares_it()
    {
        // Smart Transport's real (client-only) shape: an array rule with no IsServer.
        Assert.False(ModInfo.Parse("""{"PackageName":"SmartTransport","InstallRule":[{"Type":"Lua","Targets":["./Scripts"]}]}""")!.IsServer);
        Assert.False(ModInfo.Parse("""{"PackageName":"X","InstallRule":[{"Type":"Lua","IsServer":false}]}""")!.IsServer);
        Assert.False(ModInfo.Parse("""{"PackageName":"X"}""")!.IsServer);
    }

    [Fact]
    public void Server_flag_handles_a_lone_rule_object_defensively()
    {
        Assert.True(ModInfo.Parse("""{"PackageName":"X","InstallRule":{"IsServer":true}}""")!.IsServer);
        Assert.False(ModInfo.Parse("""{"PackageName":"X","InstallRule":{"IsClient":true}}""")!.IsServer);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]                    // no PackageName
    [InlineData("""{"Version":"1"}""")]   // no PackageName
    public void Returns_null_on_bad_input(string json) =>
        Assert.Null(ModInfo.Parse(json));
}

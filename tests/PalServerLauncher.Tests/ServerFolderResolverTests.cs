using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

/// <summary>
/// The server folder is "PalServer" for a new install but stays whatever a pre-1.1.0 install is already called.
/// Getting this wrong is expensive: picking the new name on an existing install would leave the user's world in
/// the old folder while SteamCMD downloaded a fresh server beside it, which reads as a wiped server.
/// </summary>
public class ServerFolderResolverTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), $"pal_root_{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_uses_the_new_name_when_no_legacy_folder_is_there()
    {
        var root = NewRoot();
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal("PalServer", LauncherConfig.ResolveServerFolder(root));
            Assert.Equal(Path.Combine(root, "PalServer"), LauncherConfig.ServerDir(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Resolve_uses_the_new_name_when_the_root_does_not_exist_yet()
    {
        Assert.Equal("PalServer", LauncherConfig.ResolveServerFolder(NewRoot()));
    }

    /// <summary>SteamCMD lowercases force_install_dir, so this is what a real upgraded install looks like.</summary>
    [Fact]
    public void Resolve_keeps_a_legacy_install_on_its_own_name()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "palworlddedicatedserver"));
        try
        {
            Assert.Equal("palworlddedicatedserver", LauncherConfig.ResolveServerFolder(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>The name is reported exactly as it sits on disk, so a log line naming it isn't lying.</summary>
    [Theory]
    [InlineData("PalworldDedicatedServer")]
    [InlineData("palworlddedicatedserver")]
    [InlineData("PALWORLDdedicatedSERVER")]
    public void Resolve_reports_the_real_casing_whatever_it_is(string onDisk)
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, onDisk));
        try
        {
            Assert.Equal(onDisk, LauncherConfig.ResolveServerFolder(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>Existence decides, not whether a server is installed. A legacy folder holding a broken install (or
    /// only saves) has to be repaired in place, never abandoned for a fresh install under the new name.</summary>
    [Fact]
    public void Resolve_honors_a_legacy_folder_with_no_server_exe_in_it()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "palworlddedicatedserver", "Pal", "Saved", "SaveGames", "0", "ABC"));
        try
        {
            Assert.Equal("palworlddedicatedserver", LauncherConfig.ResolveServerFolder(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>An empty leftover still wins: the user deleted their install, and pinning them to the old name is
    /// cosmetic, where guessing wrong the other way risks stranding a world.</summary>
    [Fact]
    public void Resolve_honors_an_empty_legacy_folder()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "palworlddedicatedserver"));
        try
        {
            Assert.Equal("palworlddedicatedserver", LauncherConfig.ResolveServerFolder(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Resolve_prefers_the_legacy_folder_when_both_exist()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "palworlddedicatedserver"));
        Directory.CreateDirectory(Path.Combine(root, "PalServer"));
        try
        {
            Assert.Equal("palworlddedicatedserver", LauncherConfig.ResolveServerFolder(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A folder that merely starts with the legacy name is a different folder, not our install.</summary>
    [Fact]
    public void Resolve_does_not_match_a_similarly_named_folder()
    {
        var root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "PalworldDedicatedServer.old"));
        Directory.CreateDirectory(Path.Combine(root, "MyPalworldDedicatedServer"));
        try
        {
            Assert.Equal("PalServer", LauncherConfig.ResolveServerFolder(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

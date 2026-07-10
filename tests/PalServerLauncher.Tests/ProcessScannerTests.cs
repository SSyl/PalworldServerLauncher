using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ProcessScannerTests
{
    [Fact]
    public void ExpectedExePath_builds_console_server_path()
    {
        var path = ProcessScanner.ExpectedExePath(@"D:\Palworld");
        Assert.Equal(@"D:\Palworld\PalworldDedicatedServer\Pal\Binaries\Win64\PalServer-Win64-Shipping-Cmd.exe", path);
    }

    [Fact]
    public void IsUnder_true_for_nested_path()
    {
        Assert.True(ProcessScanner.IsUnder(
            @"D:\Palworld\server\Pal\Binaries\Win64\PalServer-Win64-Shipping-Cmd.exe",
            @"D:\Palworld"));
    }

    [Fact]
    public void IsUnder_is_case_insensitive()
    {
        Assert.True(ProcessScanner.IsUnder(
            @"d:\palworld\SERVER\x.exe",
            @"D:\Palworld"));
    }

    [Fact]
    public void IsUnder_respects_folder_boundary_no_prefix_bleed()
    {
        // A sibling folder that shares a name prefix must NOT match (multi-server safety).
        Assert.False(ProcessScanner.IsUnder(
            @"D:\Palworld2\server\x.exe",
            @"D:\Palworld"));
    }

    [Fact]
    public void FindManagedServer_returns_null_when_none_running()
    {
        // No Palworld server under a bogus root -> null (and must not throw).
        var found = ProcessScanner.FindManagedServer(@"Z:\no\such\root");
        Assert.Null(found);
    }
}

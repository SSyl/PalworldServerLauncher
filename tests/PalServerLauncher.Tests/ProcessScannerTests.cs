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

    [Fact]
    public void ClassifyServerPath_managed_when_under_root()
    {
        Assert.Equal(ProcessScanner.ServerOwnership.Managed, ProcessScanner.ClassifyServerPath(
            @"D:\Palworld\PalworldDedicatedServer\Pal\Binaries\Win64\PalServer-Win64-Shipping-Cmd.exe", @"D:\Palworld"));
    }

    [Fact]
    public void ClassifyServerPath_managed_is_case_insensitive()
    {
        Assert.Equal(ProcessScanner.ServerOwnership.Managed,
            ProcessScanner.ClassifyServerPath(@"d:\palworld\SERVER\x.exe", @"D:\Palworld"));
    }

    [Fact]
    public void ClassifyServerPath_foreign_when_outside_root()
    {
        Assert.Equal(ProcessScanner.ServerOwnership.Foreign,
            ProcessScanner.ClassifyServerPath(@"C:\OtherServers\Palworld\x.exe", @"D:\Palworld"));
    }

    [Fact]
    public void ClassifyServerPath_foreign_for_prefix_sibling_not_under_root()
    {
        // A sibling that shares a name prefix is a different install -> Foreign, not Managed (multi-server safety).
        Assert.Equal(ProcessScanner.ServerOwnership.Foreign,
            ProcessScanner.ClassifyServerPath(@"D:\Palworld2\server\x.exe", @"D:\Palworld"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ClassifyServerPath_unreadable_when_path_blank(string? exePath)
    {
        // MainModule unreadable (e.g. the process is elevated), so we can neither confirm it's ours nor attach.
        Assert.Equal(ProcessScanner.ServerOwnership.Unreadable, ProcessScanner.ClassifyServerPath(exePath, @"D:\Palworld"));
    }
}

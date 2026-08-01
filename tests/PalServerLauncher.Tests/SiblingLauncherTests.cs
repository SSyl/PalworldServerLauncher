using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The same-folder rule behind the single-instance guard. Running a launcher per folder is a supported setup
/// (one server install each), so the match has to be the exact exe file, never the process name.
/// </summary>
public class SiblingLauncherTests
{
    [Fact]
    public void The_same_exe_matches()
    {
        Assert.True(ProcessScanner.SameExe(@"C:\Games\A\PalworldServerLauncher.exe", @"C:\Games\A\PalworldServerLauncher.exe"));
    }

    [Fact]
    public void The_same_exe_in_a_different_folder_does_not_match()
    {
        // The whole point: two installs side by side must both be allowed to run.
        Assert.False(ProcessScanner.SameExe(@"C:\Games\A\PalworldServerLauncher.exe", @"C:\Games\B\PalworldServerLauncher.exe"));
    }

    [Theory]
    [InlineData(@"c:\games\a\palworldserverlauncher.exe")]
    [InlineData(@"C:\Games\A\..\A\PalworldServerLauncher.exe")]
    [InlineData(@"C:\Games\A\.\PalworldServerLauncher.exe")]
    public void Equivalent_spellings_of_one_path_match(string variant)
    {
        Assert.True(ProcessScanner.SameExe(@"C:\Games\A\PalworldServerLauncher.exe", variant));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_unreadable_path_never_matches(string? unreadable)
    {
        // MainModule throws for an elevated process. Unknown must not be treated as a sibling, or the launcher
        // would refuse to start over an unrelated process it simply couldn't inspect.
        Assert.False(ProcessScanner.SameExe(unreadable, @"C:\Games\A\PalworldServerLauncher.exe"));
        Assert.False(ProcessScanner.SameExe(@"C:\Games\A\PalworldServerLauncher.exe", unreadable));
    }

    [Fact]
    public void This_test_process_is_not_its_own_sibling()
    {
        // Excluding our own PID is what stops the guard from blocking every launch.
        Assert.DoesNotContain(ProcessScanner.FindSiblingLaunchers(), p => p.Id == Environment.ProcessId);
    }
}

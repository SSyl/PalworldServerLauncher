using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

/// <summary>
/// The login-autostart shortcut's two pure decisions: which file this install owns, and whether Windows has that
/// file switched off. Creating and removing the shortcut is verified by running, not here.
/// </summary>
public class LoginShortcutTests
{
    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x00, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void An_enabled_entry_is_not_disabled(byte[] value)
    {
        Assert.False(LoginShortcut.IsDisabledValue(value));
    }

    [Theory]
    // Written by Windows 11 26200 on switching the entry off in Task Manager > Startup apps. The tail is a
    // FILETIME of when, which the decode ignores.
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x1A, 0x5C, 0xFB, 0x3D, 0x58, 0x29, 0xDD, 0x01 })]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void An_entry_with_bit_zero_set_is_disabled(byte[] value)
    {
        Assert.True(LoginShortcut.IsDisabledValue(value));
    }

    [Fact]
    public void A_missing_entry_counts_as_enabled()
    {
        // Windows only writes a value once the switch has been used, so no value is the normal enabled state.
        Assert.False(LoginShortcut.IsDisabledValue(null));
        Assert.False(LoginShortcut.IsDisabledValue([]));
    }

    [Theory]
    [InlineData(@"c:\games\a\palworldserverlauncher.exe")]
    [InlineData(@"C:\Games\A\..\A\PalworldServerLauncher.exe")]
    public void Equivalent_spellings_of_one_exe_own_the_same_shortcut(string variant)
    {
        Assert.Equal(LoginShortcut.ShortcutPath(@"C:\Games\A\PalworldServerLauncher.exe"),
            LoginShortcut.ShortcutPath(variant));
    }

    [Fact]
    public void Two_installs_own_different_shortcuts()
    {
        // Side-by-side installs are supported, so neither may overwrite or delete the other's shortcut.
        Assert.NotEqual(LoginShortcut.ShortcutPath(@"C:\Games\A\PalworldServerLauncher.exe"),
            LoginShortcut.ShortcutPath(@"C:\Games\B\PalworldServerLauncher.exe"));
    }
}

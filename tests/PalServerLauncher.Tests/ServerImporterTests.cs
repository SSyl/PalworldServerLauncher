using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ServerImporterTests
{
    [Fact]
    public void LooksLikeServerInstall_true_when_the_server_exe_is_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"import_ok_{Guid.NewGuid():N}");
        try
        {
            var exeDir = Path.Combine(dir, "Pal", "Binaries", "Win64");
            Directory.CreateDirectory(exeDir);
            File.WriteAllText(Path.Combine(exeDir, ProcessScanner.ServerProcessName + ".exe"), "");

            Assert.True(ServerImporter.LooksLikeServerInstall(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void LooksLikeServerInstall_false_for_a_folder_without_the_exe()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"import_bad_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "Pal", "Binaries", "Win64")); // structure but no exe
            Assert.False(ServerImporter.LooksLikeServerInstall(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\definitely\does\not\exist")]
    public void LooksLikeServerInstall_false_for_blank_or_missing(string dir) =>
        Assert.False(ServerImporter.LooksLikeServerInstall(dir));

    /// <summary>The Linux dedicated server as it actually lands on disk: PalServer.sh and a linux64 folder at the
    /// root, no Win64 exe, and the settings under the platform-named LinuxServer config folder (GitHub issue #9).</summary>
    private static string WriteLinuxInstall(string worldId, bool withConfig = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"import_linux_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "linux64"));
        Directory.CreateDirectory(Path.Combine(dir, "Pal", "Binaries", "Linux"));
        File.WriteAllText(Path.Combine(dir, "PalServer.sh"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(dir, "steamclient.so"), "");

        var world = Path.Combine(dir, ServerImporter.SaveGamesRelative, "0", worldId);
        Directory.CreateDirectory(world);
        File.WriteAllText(Path.Combine(world, "Level.sav"), "level");
        File.WriteAllText(Path.Combine(world, "LevelMeta.sav"), "meta");

        if (withConfig)
        {
            var cfg = Path.Combine(dir, ServerImporter.LinuxConfigRelative);
            Directory.CreateDirectory(cfg);
            File.WriteAllText(Path.Combine(cfg, "PalWorldSettings.ini"), "[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=()\r\n");
            File.WriteAllText(Path.Combine(cfg, "GameUserSettings.ini"), $"[/Script/Pal.PalGameLocalSettings]\r\nDedicatedServerName={worldId}\r\n");
        }
        return dir;
    }

    [Fact]
    public void DetectInstallKind_recognizes_a_linux_install()
    {
        var dir = WriteLinuxInstall("ABC123");
        try
        {
            Assert.Equal(ServerInstallKind.Linux, ServerImporter.DetectInstallKind(dir));
            Assert.False(ServerImporter.LooksLikeServerInstall(dir)); // it is not runnable here
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void DetectInstallKind_prefers_windows_when_both_are_present()
    {
        var dir = WriteLinuxInstall("ABC123");
        try
        {
            var exeDir = Path.Combine(dir, "Pal", "Binaries", "Win64");
            Directory.CreateDirectory(exeDir);
            File.WriteAllText(Path.Combine(exeDir, ProcessScanner.ServerProcessName + ".exe"), "");

            Assert.Equal(ServerInstallKind.Windows, ServerImporter.DetectInstallKind(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Z:\definitely\does\not\exist")]
    public void DetectInstallKind_none_for_blank_or_missing(string dir) =>
        Assert.Equal(ServerInstallKind.None, ServerImporter.DetectInstallKind(dir));

    [Fact]
    public async Task CopyWorldAndSettingsAsync_moves_the_world_and_both_inis_into_the_windows_config_folder()
    {
        const string worldId = "FDD3EE684AEC6FCB1BCB53A576EBB0E4";
        var source = WriteLinuxInstall(worldId);
        var dest = Path.Combine(Path.GetTempPath(), $"import_dst_{Guid.NewGuid():N}");
        try
        {
            var payload = await ServerImporter.CopyWorldAndSettingsAsync(
                source, dest, ServerImporter.LinuxConfigRelative, null, CancellationToken.None);

            Assert.True(payload.Saves);
            Assert.Equal(["PalWorldSettings.ini", "GameUserSettings.ini"], payload.ConfigFiles);
            Assert.Equal("level", File.ReadAllText(Path.Combine(dest, ServerImporter.SaveGamesRelative, "0", worldId, "Level.sav")));
            Assert.True(File.Exists(Path.Combine(dest, ServerImporter.WindowsConfigRelative, "PalWorldSettings.ini")));

            // The pointer at the world has to land where the Windows server reads it, or the copied save is invisible.
            var gus = File.ReadAllText(Path.Combine(dest, ServerImporter.WindowsConfigRelative, GameUserSettingsFile.FileName));
            Assert.Equal(worldId, GameUserSettingsFile.ReadWorldId(gus));
            Assert.False(Directory.Exists(Path.Combine(dest, ServerImporter.LinuxConfigRelative)));

            // Nothing else rides along: the Linux binaries are useless here.
            Assert.False(File.Exists(Path.Combine(dest, "PalServer.sh")));
            Assert.False(Directory.Exists(Path.Combine(dest, "linux64")));
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task CopyWorldAndSettingsAsync_reports_what_was_missing()
    {
        var source = WriteLinuxInstall("ABC123", withConfig: false);
        var dest = Path.Combine(Path.GetTempPath(), $"import_dst_{Guid.NewGuid():N}");
        try
        {
            var payload = await ServerImporter.CopyWorldAndSettingsAsync(
                source, dest, ServerImporter.LinuxConfigRelative, null, CancellationToken.None);

            Assert.True(payload.Saves);
            Assert.Empty(payload.ConfigFiles);
        }
        finally
        {
            Directory.Delete(source, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }

    [Fact]
    public async Task CopyDirectoryAsync_copies_all_files_preserving_structure()
    {
        var source = Path.Combine(Path.GetTempPath(), $"import_src_{Guid.NewGuid():N}");
        var dest = Path.Combine(Path.GetTempPath(), $"import_dst_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(source, "Pal", "Saved"));
            File.WriteAllText(Path.Combine(source, "DefaultPalWorldSettings.ini"), "root");
            File.WriteAllText(Path.Combine(source, "Pal", "Saved", "world.sav"), "save");

            await ServerImporter.CopyDirectoryAsync(source, dest, null, CancellationToken.None);

            Assert.Equal("root", File.ReadAllText(Path.Combine(dest, "DefaultPalWorldSettings.ini")));
            Assert.Equal("save", File.ReadAllText(Path.Combine(dest, "Pal", "Saved", "world.sav")));
        }
        finally
        {
            if (Directory.Exists(source)) Directory.Delete(source, recursive: true);
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        }
    }
}

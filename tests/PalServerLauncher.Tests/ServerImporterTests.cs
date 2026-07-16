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

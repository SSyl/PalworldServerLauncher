using System.Collections.Generic;
using System.IO;
using System.Linq;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

public class GameSettingsServiceTests
{
    private static string WriteIni(string optionSettingsInner)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pal_gs_{Guid.NewGuid():N}");
        var cfgDir = Path.Combine(root, LauncherConfig.ServerFolderName, "Pal", "Saved", "Config", "WindowsServer");
        Directory.CreateDirectory(cfgDir);
        File.WriteAllText(Path.Combine(cfgDir, "PalWorldSettings.ini"),
            "[/Script/Pal.PalGameWorldSettings]\r\nOptionSettings=(" + optionSettingsInner + ")\r\n");
        return root;
    }

    [Fact]
    public void LoadExtras_returns_only_non_catalog_keys()
    {
        // FutureTuple stands in for a not-yet-catalogued key a game update might add (CrossplayPlatforms is
        // now a first-class Raw catalog key, so it is no longer "extra").
        var root = WriteIni("ExpRate=2.000000,FutureParam=5,FutureTuple=(A,B)");
        try
        {
            var svc = new GameSettingsService(root, new Logger(verbose: false));
            var extras = svc.LoadExtras();
            var keys = extras.Select(x => x.Key).ToList();

            Assert.Contains("FutureParam", keys);
            Assert.Contains("FutureTuple", keys);
            Assert.DoesNotContain("ExpRate", keys);            // ExpRate is a catalog key
            Assert.DoesNotContain("CrossplayPlatforms", keys); // now catalogued (Raw), not extra
            Assert.Equal("(A,B)", extras.First(x => x.Key == "FutureTuple").Value);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SaveExtras_writes_the_value_and_leaves_catalog_keys_alone()
    {
        var root = WriteIni("ExpRate=2.000000,FutureParam=5");
        try
        {
            var svc = new GameSettingsService(root, new Logger(verbose: false));
            Assert.True(svc.SaveExtras(new Dictionary<string, string> { ["FutureParam"] = "9" }, serverRunning: false));

            Assert.Equal("9", svc.LoadExtras().First(x => x.Key == "FutureParam").Value);
            Assert.Equal("2.000000", svc.Load()["ExpRate"]); // catalog key untouched
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void SaveExtras_refuses_a_value_that_would_corrupt_the_blob()
    {
        var root = WriteIni("FutureParam=5,Another=1");
        try
        {
            var svc = new GameSettingsService(root, new Logger(verbose: false));
            // A stray top-level comma would split the value and silently drop part of it - reject.
            Assert.False(svc.SaveExtras(new Dictionary<string, string> { ["FutureParam"] = "5,000" }, serverRunning: false));
            Assert.Equal("5", svc.LoadExtras().First(x => x.Key == "FutureParam").Value); // unchanged
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Save_writes_a_raw_tuple_verbatim_without_quoting()
    {
        var root = WriteIni("CrossplayPlatforms=(Steam,Xbox,PS5,Mac),ExpRate=1.000000");
        try
        {
            var svc = new GameSettingsService(root, new Logger(verbose: false));
            Assert.True(svc.Save(new Dictionary<string, string> { ["CrossplayPlatforms"] = "(Steam,PS5)" }, serverRunning: false));

            Assert.Equal("(Steam,PS5)", svc.Load()["CrossplayPlatforms"]); // read back verbatim, not quoted
            Assert.Equal("1.000000", svc.Load()["ExpRate"]);               // neighbour untouched
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Save_refuses_a_raw_value_that_would_corrupt_the_blob()
    {
        var root = WriteIni("CrossplayPlatforms=(Steam,Xbox),ExpRate=1.000000");
        try
        {
            var svc = new GameSettingsService(root, new Logger(verbose: false));
            // An unbalanced parenthesis would swallow the following key - reject, name it, change nothing.
            Assert.False(svc.Save(new Dictionary<string, string> { ["CrossplayPlatforms"] = "(Steam,Xbox" }, serverRunning: false, out var badKey));
            Assert.Equal("CrossplayPlatforms", badKey);
            Assert.Equal("(Steam,Xbox)", svc.Load()["CrossplayPlatforms"]); // unchanged
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}

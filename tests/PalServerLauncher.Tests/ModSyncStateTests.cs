using System.IO;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ModSyncStateTests
{
    [Fact]
    public void NeedsSync_true_when_never_recorded()
    {
        Assert.True(ModSyncState.NeedsSync(recorded: null, liveManifest: "abc", forced: false, folderPresent: true));
    }

    [Fact]
    public void NeedsSync_true_when_folder_missing_even_if_manifest_matches()
    {
        var recorded = new ModSyncEntry { Manifest = "abc", Forced = false };
        Assert.True(ModSyncState.NeedsSync(recorded, "abc", forced: false, folderPresent: false));
    }

    [Fact]
    public void NeedsSync_true_when_manifest_changed()
    {
        var recorded = new ModSyncEntry { Manifest = "abc", Forced = true };
        Assert.True(ModSyncState.NeedsSync(recorded, "def", forced: true, folderPresent: true));
    }

    [Fact]
    public void NeedsSync_true_when_force_state_changed()
    {
        var recorded = new ModSyncEntry { Manifest = "abc", Forced = false };
        Assert.True(ModSyncState.NeedsSync(recorded, "abc", forced: true, folderPresent: true));
    }

    [Fact]
    public void NeedsSync_false_when_manifest_and_force_match_and_folder_present()
    {
        var recorded = new ModSyncEntry { Manifest = "abc", Forced = true };
        Assert.False(ModSyncState.NeedsSync(recorded, "abc", forced: true, folderPresent: true));
    }

    [Fact]
    public void Round_trips_through_disk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "psl-modsync-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var path = ModSyncState.PathFor(dir);
            var state = new ModSyncState();
            state.Items["123"] = new ModSyncEntry { Manifest = "m1", Forced = true };
            state.Save(path);

            var loaded = ModSyncState.Load(path);
            Assert.True(loaded.Items.ContainsKey("123"));
            Assert.Equal("m1", loaded.Items["123"].Manifest);
            Assert.True(loaded.Items["123"].Forced);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_empty_when_file_absent()
    {
        var path = Path.Combine(Path.GetTempPath(), "psl-missing-" + Path.GetRandomFileName(), "mod-sync.json");
        Assert.Empty(ModSyncState.Load(path).Items);
    }
}

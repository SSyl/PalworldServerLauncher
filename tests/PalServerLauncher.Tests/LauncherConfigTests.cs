using PalServerLauncher.Config;

namespace PalServerLauncher.Tests;

public class LauncherConfigTests
{
    [Fact]
    public void Load_missing_file_returns_defaults()
    {
        var cfg = LauncherConfig.Load(@"Z:\does\not\exist\launcher.json");

        Assert.True(cfg.RestartOnCrash);
        Assert.False(cfg.ScheduledRestartEnabled);
        Assert.Equal(new[] { new TimeOnly(3, 0) }, cfg.RestartTimes);
        Assert.Equal(7, cfg.BackupRetentionDays);
        Assert.False(cfg.DiscordEnabled);

        Assert.True(cfg.RestartBroadcastEnabled);
        Assert.Equal(new[] { 15, 5, 1 }, cfg.RestartBroadcastLeadMinutes);
        Assert.True(cfg.AutoUpdateEnabled);
        Assert.False(cfg.VersionPinEnabled);
        Assert.Equal("", cfg.PinnedBuildId);
        Assert.True(cfg.HideSteamCmdWindow);
        Assert.False(cfg.VerifyOnUpdate);
        Assert.False(cfg.LogHealthStats);
        Assert.True(cfg.ZombieCheckEnabled);
        Assert.Equal(10, cfg.ZombieFailureThreshold);

        Assert.True(cfg.BackupOnStartup);
        Assert.True(cfg.BackupOnShutdown);
        Assert.False(cfg.ScheduledBackupEnabled);
        Assert.Empty(cfg.BackupTimes);

        Assert.Equal("Server restart in {minutes} minutes", cfg.RestartAnnounceMessage);
        Assert.Equal("Server update available. Restarting server in {minutes} minutes", cfg.UpdateAnnounceMessage);

        Assert.Equal("en", cfg.Language);
        Assert.False(cfg.AutoReconnectSingleInstance);
    }

    [Fact]
    public void Save_then_Load_round_trips_values()
    {
        var path = Path.Combine(Path.GetTempPath(), $"launcher_test_{Guid.NewGuid():N}.json");
        try
        {
            var original = new LauncherConfig
            {
                ServerRoot = @"D:\Palworld",
                ScheduledRestartEnabled = true,
                RestartTimes = new() { new TimeOnly(3, 30), new TimeOnly(15, 30) },
                MinUptimeBeforeRestart = TimeSpan.FromHours(1),
                BackupRetentionDays = 14,
                DiscordEnabled = true,
                DiscordWebhookUrl = "https://discord.example/webhook",
                RestartBroadcastEnabled = false,
                RestartBroadcastLeadMinutes = new() { 15, 5 },
                AutoUpdateEnabled = false,
                VersionPinEnabled = true,
                PinnedBuildId = "12345678",
                HideSteamCmdWindow = true,
                VerifyOnUpdate = true,
                LogHealthStats = true,
                AutoReconnectSingleInstance = true,
                BackupOnStartup = false,
                ScheduledBackupEnabled = true,
                BackupTimes = new() { new TimeOnly(4, 0) },
                RestartAnnounceMessage = "Down in {minutes}!",
                UpdateAnnounceMessage = "Update! {minutes}",
            };
            original.Save(path);

            var loaded = LauncherConfig.Load(path);

            Assert.Equal(@"D:\Palworld", loaded.ServerRoot);
            Assert.True(loaded.ScheduledRestartEnabled);
            Assert.Equal(new[] { new TimeOnly(3, 30), new TimeOnly(15, 30) }, loaded.RestartTimes);
            Assert.Equal(TimeSpan.FromHours(1), loaded.MinUptimeBeforeRestart);
            Assert.Equal(14, loaded.BackupRetentionDays);
            Assert.True(loaded.DiscordEnabled);
            Assert.Equal("https://discord.example/webhook", loaded.DiscordWebhookUrl);
            Assert.False(loaded.RestartBroadcastEnabled);
            Assert.Equal(new[] { 15, 5 }, loaded.RestartBroadcastLeadMinutes);
            Assert.False(loaded.AutoUpdateEnabled);
            Assert.True(loaded.VersionPinEnabled);
            Assert.Equal("12345678", loaded.PinnedBuildId);
            Assert.True(loaded.HideSteamCmdWindow);
            Assert.True(loaded.VerifyOnUpdate);
            Assert.True(loaded.LogHealthStats);
            Assert.True(loaded.AutoReconnectSingleInstance);
            Assert.False(loaded.BackupOnStartup);
            Assert.True(loaded.ScheduledBackupEnabled);
            Assert.Equal(new[] { new TimeOnly(4, 0) }, loaded.BackupTimes);
            Assert.Equal("Down in {minutes}!", loaded.RestartAnnounceMessage);
            Assert.Equal("Update! {minutes}", loaded.UpdateAnnounceMessage);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_self_heals_an_older_file_missing_new_fields()
    {
        var path = Path.Combine(Path.GetTempPath(), $"launcher_migrate_{Guid.NewGuid():N}.json");
        try
        {
            // Simulate a file written by an older launcher version: only a couple of fields.
            File.WriteAllText(path, "{ \"ServerRoot\": \"D:\\\\Pal\", \"BackupRetentionDays\": 3 }");

            var loaded = LauncherConfig.Load(path);

            // Existing values are preserved...
            Assert.Equal(@"D:\Pal", loaded.ServerRoot);
            Assert.Equal(3, loaded.BackupRetentionDays);

            // ...and the on-disk file now contains the new fields (rewritten to the current schema).
            var rewritten = File.ReadAllText(path);
            Assert.Contains("\"ScheduledBackupEnabled\"", rewritten);
            Assert.Contains("\"BackupOnStartup\"", rewritten);
            Assert.Contains("\"AutoUpdateEnabled\"", rewritten);
            Assert.Contains("\"LogHealthStats\"", rewritten);

            // And re-loading the healed file is a no-op (stable - no repeated rewrites).
            var before = File.GetLastWriteTimeUtc(path);
            LauncherConfig.Load(path);
            Assert.Equal(before, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_repoints_old_exe_folder_ServerRoot_to_the_data_folder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"launcher_root_{Guid.NewGuid():N}.json");
        try
        {
            // A config from before the split, whose ServerRoot was the exe folder.
            new LauncherConfig { ServerRoot = AppContext.BaseDirectory }.Save(path);
            var loaded = LauncherConfig.Load(path);
            Assert.Equal(LauncherConfig.DataRoot, loaded.ServerRoot);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Load_corrupt_json_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"launcher_bad_{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ this is not valid json ");
            var cfg = LauncherConfig.Load(path);
            Assert.Equal(7, cfg.BackupRetentionDays); // default, not a throw
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

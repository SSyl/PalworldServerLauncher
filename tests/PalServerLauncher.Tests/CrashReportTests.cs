using System;
using System.IO;
using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class CrashReportTests
{
    /// <summary>A server root with one crash folder per entry, stamped in the order given.</summary>
    private static string WriteCrashFolders(params (string Name, string? Context)[] folders)
    {
        var root = Path.Combine(Path.GetTempPath(), $"pal_cr_{Guid.NewGuid():N}");
        var crashes = Path.Combine(LauncherConfig.ServerDir(root), "Pal", "Saved", "Crashes");
        var stamp = DateTime.UtcNow;
        foreach (var (name, context) in folders)
        {
            var dir = Path.Combine(crashes, name);
            Directory.CreateDirectory(dir);
            if (context is null)
                continue;
            var path = Path.Combine(dir, CrashReport.ContextFileName);
            File.WriteAllText(path, context);
            File.SetLastWriteTimeUtc(path, stamp);
            stamp = stamp.AddSeconds(1); // later entries are newer
        }
        return root;
    }

    private static string Context(string message) =>
        $"<FGenericCrashContext><RuntimeProperties><ErrorMessage>{message}</ErrorMessage></RuntimeProperties></FGenericCrashContext>";

    [Fact]
    public async Task ReadAsync_reports_the_folder_even_when_the_context_is_truncated()
    {
        // The dying process can be killed mid-write. The reason is lost, but the folder still holds the
        // minidump, so pointing at it beats reporting nothing and implying the server was force-stopped.
        var root = WriteCrashFolders(("UECC-Windows-AAA_0000", "<FGenericCrashContext><ErrorMessage>Save data is cor"));

        var crash = await CrashReport.ReadAsync(root, DateTime.UtcNow.AddMinutes(-1));

        Assert.NotNull(crash);
        Assert.Null(crash!.Value.Reason);
        Assert.EndsWith("UECC-Windows-AAA_0000", crash.Value.Directory);
    }

    [Fact]
    public async Task ReadAsync_falls_through_to_an_older_folder_when_the_newest_has_no_reason()
    {
        var root = WriteCrashFolders(
            ("UECC-Windows-OLD_0000", Context("LowLevelFatalError: the real reason")),
            ("UECC-Windows-NEW_0000", "<FGenericCrashContext>truncated"));

        var crash = await CrashReport.ReadAsync(root, DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal("LowLevelFatalError: the real reason", crash!.Value.Reason);
        Assert.EndsWith("UECC-Windows-OLD_0000", crash.Value.Directory);
    }

    [Fact]
    public async Task ReadAsync_ignores_folders_from_before_this_run()
    {
        var root = WriteCrashFolders(("UECC-Windows-STALE_0000", Context("An older crash")));

        Assert.Null(await CrashReport.ReadAsync(root, DateTime.UtcNow.AddMinutes(5)));
    }

    [Fact]
    public async Task ReadAsync_returns_null_when_nothing_crashed() =>
        Assert.Null(await CrashReport.ReadAsync(
            Path.Combine(Path.GetTempPath(), $"pal_cr_missing_{Guid.NewGuid():N}"), DateTime.UtcNow.AddMinutes(-1)));

    // Verbatim from a real Palworld 1.0.2 crash, produced by truncating Level.sav (issue #11).
    private const string RealContext = """
        <?xml version="1.0" encoding="UTF-8"?>
        <FGenericCrashContext>
        	<RuntimeProperties>
        		<CrashType>Assert</CrashType>
        		<ErrorMessage>LowLevelFatalError [File:C:\works\Pal-UE-App\Source\Pal\PalSaveGameManager.cpp] [Line: 2053] Error: Save data is corrupted. Please restore from a backup. 0/1324D128B45241668097DB8082268633/Level</ErrorMessage>
        		<GameName>UE-Pal</GameName>
        	</RuntimeProperties>
        </FGenericCrashContext>
        """;

    [Fact]
    public void ExtractErrorMessage_pulls_the_reason_from_a_real_crash_context()
    {
        var reason = CrashReport.ExtractErrorMessage(RealContext);

        Assert.NotNull(reason);
        Assert.Contains("Save data is corrupted. Please restore from a backup.", reason);
        Assert.StartsWith("LowLevelFatalError", reason);
    }

    [Fact]
    public void ExtractErrorMessage_decodes_escaped_entities()
    {
        Assert.Equal(
            """Assertion failed: Name != "" [Line: 12]""",
            CrashReport.ExtractErrorMessage(
                """<ErrorMessage>Assertion failed: Name != &quot;&quot; [Line: 12]</ErrorMessage>"""));
    }

    [Fact]
    public void ExtractErrorMessage_collapses_multiline_text_to_one_line()
    {
        Assert.Equal(
            "Fatal error: something broke here",
            CrashReport.ExtractErrorMessage("<ErrorMessage>Fatal error:\n\tsomething broke\r\n   here\n</ErrorMessage>"));
    }

    [Theory]
    // A context truncated by the dying process must come back empty rather than throw.
    [InlineData("<FGenericCrashContext><ErrorMessage>Save data is cor")]
    [InlineData("<FGenericCrashContext><CrashType>Assert</CrashType></FGenericCrashContext>")]
    [InlineData("<ErrorMessage></ErrorMessage>")]
    [InlineData("<ErrorMessage>   \n  </ErrorMessage>")]
    [InlineData("")]
    public void ExtractErrorMessage_returns_null_when_there_is_no_usable_reason(string xml) =>
        Assert.Null(CrashReport.ExtractErrorMessage(xml));

    [Fact]
    public void SelectCrashDirs_ignores_folders_written_before_this_run_started()
    {
        var launched = new DateTime(2026, 8, 6, 12, 00, 00, DateTimeKind.Utc);
        (string, DateTime)[] dirs =
        [
            ("UECC-old", launched.AddMinutes(-30)),
            ("UECC-older", launched.AddDays(-3)),
        ];

        Assert.Empty(CrashReport.SelectCrashDirs(dirs, launched));
    }

    [Fact]
    public void SelectCrashDirs_orders_this_run_newest_first_so_a_bad_context_can_fall_through()
    {
        var launched = new DateTime(2026, 8, 6, 12, 00, 00, DateTimeKind.Utc);
        (string, DateTime)[] dirs =
        [
            ("UECC-stale", launched.AddHours(-2)),
            ("UECC-this-run", launched.AddSeconds(3)),
            ("UECC-earlier-this-run", launched.AddSeconds(1)),
        ];

        Assert.Equal(
            ["UECC-this-run", "UECC-earlier-this-run"],
            CrashReport.SelectCrashDirs(dirs, launched));
    }

    [Fact]
    public void SelectCrashDirs_accepts_a_folder_stamped_exactly_at_launch()
    {
        var launched = new DateTime(2026, 8, 6, 12, 00, 00, DateTimeKind.Utc);

        Assert.Equal(["UECC-boundary"], CrashReport.SelectCrashDirs([("UECC-boundary", launched)], launched));
    }

    [Fact]
    public void SelectCrashDirs_returns_nothing_when_this_run_did_not_crash()
    {
        Assert.Empty(CrashReport.SelectCrashDirs([], DateTime.UtcNow));
    }

    [Fact]
    public void DescribeExit_calls_out_a_startup_death_and_keeps_sub_second_precision()
    {
        Assert.Equal(
            "Server exited unexpectedly during startup, 6.8s after launch (exit code 3).",
            CrashReport.DescribeExit(3, TimeSpan.FromSeconds(6.83), reachedRunning: false));
    }

    [Fact]
    public void DescribeExit_reports_uptime_once_the_server_was_running()
    {
        Assert.Equal(
            "Server exited unexpectedly after 4h 12m (exit code 1).",
            CrashReport.DescribeExit(1, new TimeSpan(4, 12, 30), reachedRunning: true));
    }

    [Fact]
    public void DescribeExit_omits_the_code_when_it_could_not_be_read()
    {
        Assert.Equal(
            "Server exited unexpectedly after 5m 3s.",
            CrashReport.DescribeExit(null, new TimeSpan(0, 5, 3), reachedRunning: true));
    }

    [Theory]
    [InlineData(0.4, "0.4s")]
    [InlineData(59.9, "59.9s")]
    [InlineData(60, "1m 0s")]
    [InlineData(3599, "59m 59s")]
    [InlineData(3600, "1h 0m")]
    [InlineData(93784, "26h 3m")]
    public void FormatUptime_switches_units_at_a_minute_and_an_hour(double seconds, string expected) =>
        Assert.Equal(expected, CrashReport.FormatUptime(TimeSpan.FromSeconds(seconds)));
}

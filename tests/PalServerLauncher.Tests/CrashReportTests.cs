using System;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class CrashReportTests
{
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
    public void SelectCrashDir_ignores_folders_written_before_this_run_started()
    {
        var launched = new DateTime(2026, 8, 6, 12, 00, 00, DateTimeKind.Utc);
        (string, DateTime)[] dirs =
        [
            ("UECC-old", launched.AddMinutes(-30)),
            ("UECC-older", launched.AddDays(-3)),
        ];

        Assert.Null(CrashReport.SelectCrashDir(dirs, launched));
    }

    [Fact]
    public void SelectCrashDir_takes_the_newest_folder_from_this_run()
    {
        var launched = new DateTime(2026, 8, 6, 12, 00, 00, DateTimeKind.Utc);
        (string, DateTime)[] dirs =
        [
            ("UECC-stale", launched.AddHours(-2)),
            ("UECC-this-run", launched.AddSeconds(3)),
            ("UECC-earlier-this-run", launched.AddSeconds(1)),
        ];

        Assert.Equal("UECC-this-run", CrashReport.SelectCrashDir(dirs, launched));
    }

    [Fact]
    public void SelectCrashDir_accepts_a_folder_stamped_exactly_at_launch()
    {
        var launched = new DateTime(2026, 8, 6, 12, 00, 00, DateTimeKind.Utc);

        Assert.Equal("UECC-boundary", CrashReport.SelectCrashDir([("UECC-boundary", launched)], launched));
    }

    [Fact]
    public void SelectCrashDir_returns_null_when_nothing_crashed()
    {
        Assert.Null(CrashReport.SelectCrashDir([], DateTime.UtcNow));
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

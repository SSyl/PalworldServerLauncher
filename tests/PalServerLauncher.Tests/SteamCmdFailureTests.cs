using PalServerLauncher.Core;
using PalServerLauncher.ViewModels;

namespace PalServerLauncher.Tests;

/// <summary>
/// Reading a failed update out of SteamCMD's content log, and sizing the re-download offered afterward.
/// The sample is the real log from issue #15, where the console output said only "state is 0x6".
/// </summary>
public class SteamCmdFailureTests
{
    private static readonly string[] Issue15 =
    [
        "[2026-08-12 14:42:22] Client version: 1785799152",
        "[2026-08-12 14:42:22] Loaded 2 apps from install folder \"c:\\...\\appmanifest_*.acf\".",
        "[2026-08-12 14:42:23] AppID 2394010 state changed : Update Required,Fully Installed,Update Queued,",
        "[2026-08-12 14:42:23] AppID 228980 state changed : Update Required,Update Running,",
        "[2026-08-12 14:42:24] CDepotDownloadMgr::BYldRequestDepotManifest(App: 2394010, Depot: 2394011, Manifest: 2329769030848955382, branch: ): Failed to get manifest request code, 'Access Denied'",
        "[2026-08-12 14:42:24] AppID 2394010 update canceled : Failed downloading 1 manifests (No connection)",
        "[2026-08-12 14:42:24] AppID 2394010 App update changed : None",
        "[2026-08-12 14:42:24] AppID 2394010 scheduler finished : removed from schedule (result No connection, state 0xe) ",
    ];

    private static readonly DateTime RunStart = new(2026, 8, 12, 14, 42, 20);

    [Fact]
    public void The_refused_manifest_is_surfaced()
    {
        var reasons = SteamCmd.ExtractFailureReasons(Issue15, SteamCmd.AppId, RunStart);

        Assert.Contains(reasons, r => r.Contains("Failed to get manifest request code, 'Access Denied'"));
        Assert.Contains(reasons, r => r.Contains("update canceled"));
    }

    [Fact]
    public void Another_app_failing_is_not_reported_as_ours()
    {
        // 228980 is the Steamworks redistributable, which fails alongside ours and means nothing to the user.
        Assert.DoesNotContain(SteamCmd.ExtractFailureReasons(Issue15, SteamCmd.AppId, RunStart), r => r.Contains("228980"));
    }

    [Fact]
    public void Another_apps_depot_failure_is_not_reported_as_ours()
    {
        // The depot lines name their app as "App: <id>". Matching the class name alone let the redistributable's
        // failure be shown, top-billed, as the reason the server update failed.
        string[] contentLog =
        [
            "[2026-08-12 14:42:24] CDepotDownloadMgr::BYldRequestDepotManifest(App: 228980, Depot: 228990, Manifest: 1, branch: ): Failed to get manifest request code, 'Access Denied'",
            "[2026-08-12 14:42:24] AppID 2394010 update canceled : Failed downloading 1 manifests (No connection)",
        ];

        var reasons = SteamCmd.ExtractFailureReasons(contentLog, SteamCmd.AppId, RunStart);

        Assert.Equal(["AppID 2394010 update canceled : Failed downloading 1 manifests (No connection)"], reasons);
    }

    [Fact]
    public void A_failure_from_an_earlier_run_is_not_reported_as_this_one()
    {
        // SteamCMD appends to content_log.txt forever, so yesterday's failure sits above today's success.
        Assert.Empty(SteamCmd.ExtractFailureReasons(Issue15, SteamCmd.AppId, new DateTime(2026, 8, 13, 9, 0, 0)));
    }

    [Fact]
    public void A_healthy_log_reports_nothing()
    {
        string[] clean =
        [
            "[2026-08-12 18:09:20] AppID 2394010 state changed : Fully Installed,",
            "[2026-08-12 18:09:20] AppID 2394010 scheduler finished : removed from schedule (result No Error, state 0x0)",
        ];

        Assert.Empty(SteamCmd.ExtractFailureReasons(clean, SteamCmd.AppId, new DateTime(2026, 8, 12, 18, 0, 0)));
    }

    [Fact]
    public void The_reason_drops_the_timestamp_it_was_written_with()
    {
        // The logger stamps its own line, and the dialog shows these under a heading, so a second time is noise.
        var reasons = SteamCmd.ExtractFailureReasons(Issue15, SteamCmd.AppId, RunStart);

        Assert.All(reasons, r => Assert.DoesNotContain("2026-08-12", r));
        Assert.Contains(reasons, r => r.StartsWith("AppID 2394010 update canceled"));
    }

    [Fact]
    public void Noise_SteamCMD_prints_on_every_run_is_not_a_reason()
    {
        // LinuxGSM lists all of these as safe to ignore. Showing one as the cause would send a user off fixing
        // something that was never broken.
        string[] noisy =
        [
            "[2026-08-12 14:42:22] [S_API FAIL] SteamAPI_Init() failed; SteamAPI_IsSteamRunning() failed.",
            "[2026-08-12 14:42:22] CWorkThreadPool::~CWorkThreadPool: work processing queue not empty: 2 items discarded.",
            "[2026-08-12 14:42:24] AppID 2394010 update canceled : Failed downloading 1 manifests (No connection)",
        ];

        var reasons = SteamCmd.ExtractFailureReasons(noisy, SteamCmd.AppId, RunStart);

        Assert.Equal(["AppID 2394010 update canceled : Failed downloading 1 manifests (No connection)"], reasons);
    }

    [Fact]
    public void Only_the_last_few_reasons_are_kept()
    {
        var noisy = Enumerable.Range(0, 50).Select(i => $"AppID 2394010 update canceled : failure {i}").ToArray();

        var reasons = SteamCmd.ExtractFailureReasons(noisy, SteamCmd.AppId, RunStart);

        Assert.Equal(3, reasons.Count);
        Assert.EndsWith("failure 49", reasons[^1]);
    }

    [Fact]
    public void A_run_that_reached_steam_is_offered_the_repair()
    {
        // The console line from issue #15. Steam evaluated the app, so clearing the manifest is worth trying.
        string[] console = ["[2026-08-12 14:42:25] Error! App '2394010' state is 0x6 after update job."];

        Assert.True(SteamCmd.RanUpdateJob(console, SteamCmd.AppId, RunStart));
    }

    [Fact]
    public void A_run_that_never_reached_steam_is_not()
    {
        // No update job means no app state was ever evaluated, and a redownload would fail the same way.
        string[] console =
        [
            "[2026-08-12 14:42:21] Connecting anonymously to Steam Public...",
            "[2026-08-12 14:42:51] Could not connect to Steam.",
        ];

        Assert.False(SteamCmd.RanUpdateJob(console, SteamCmd.AppId, RunStart));
    }

    [Fact]
    public void A_three_digit_state_is_read_whole()
    {
        // Measured on SteamCMD 1785799152, updating over a server that was still running. LinuxGSM prints this
        // state without an App '<id>', which is what an earlier pass here matched on. Real output names the app.
        string[] console = ["[2026-08-12 22:36:41] Error! App '2394010' state is 0x602 after update job."];

        Assert.True(SteamCmd.RanUpdateJob(console, SteamCmd.AppId, RunStart));
        Assert.Equal("0x602", SteamCmd.ExtractUpdateState(console, SteamCmd.AppId, RunStart));
    }

    [Fact]
    public void Another_apps_state_is_never_read_as_ours()
    {
        // 228980 updates alongside the server. Its state names its own app, so it must not be picked up.
        string[] console = ["[2026-08-12 14:42:25] Error! App '228980' state is 0x202 after update job."];

        Assert.Null(SteamCmd.ExtractUpdateState(console, SteamCmd.AppId, RunStart));
        Assert.False(SteamCmd.RanUpdateJob(console, SteamCmd.AppId, RunStart));
    }

    [Fact]
    public void An_update_job_from_an_earlier_run_does_not_offer_the_repair()
    {
        string[] console = ["[2026-08-11 09:00:00] Error! App '2394010' state is 0x6 after update job."];

        Assert.False(SteamCmd.RanUpdateJob(console, SteamCmd.AppId, RunStart));
    }

    [Theory]
    // Only the two bits that describe the install rather than the attempt earn an answer. Everything else is
    // Unknown, because the state records what the app was when SteamCMD gave up and not why it did.
    [InlineData("0x626", SteamCmdProblem.DamagedFiles)]   // Files Missing
    [InlineData("0x6A6", SteamCmdProblem.DamagedFiles)]   // Files Missing + Files Corrupt
    [InlineData("0x6a6", SteamCmdProblem.DamagedFiles)]
    [InlineData("0x6", SteamCmdProblem.Unknown)]          // issue #15
    [InlineData("0x602", SteamCmdProblem.Unknown)]        // measured: a locked file
    [InlineData("0x202", SteamCmdProblem.Unknown)]
    [InlineData("0x606", SteamCmdProblem.Unknown)]
    [InlineData("0x10E", SteamCmdProblem.Unknown)]
    [InlineData("garbage", SteamCmdProblem.Unknown)]
    [InlineData(null, SteamCmdProblem.Unknown)]
    public void Only_damaged_files_is_read_off_the_state(string? state, SteamCmdProblem expected)
    {
        Assert.Equal(expected, SteamCmd.ClassifyUpdateState(state));
    }

    [Theory]
    // 0x6 is Update Required + Fully Installed and nothing else, so the run never got as far as writing.
    // Every other documented failure state carries one of Update Running, Update Paused or Update Started.
    // 0x602 was measured on a run over a live server: content_log named it "Update Required,Update Paused,
    // Update Started," in the same line that reported the hex.
    [InlineData("0x6", false)]
    [InlineData("0x202", true)]
    [InlineData("0x402", true)]
    [InlineData("0x602", true)]
    [InlineData("0x606", true)]
    [InlineData("0x626", true)]
    [InlineData("0x6A6", true)]
    [InlineData("0x10E", true)]
    public void Whether_the_run_touched_the_install_comes_from_the_bits(string state, bool changed)
    {
        Assert.Equal(changed, SteamCmd.StateChangedFiles(state));
    }

    [Fact]
    public void An_unreadable_state_says_nothing_about_the_install()
    {
        Assert.Null(SteamCmd.StateChangedFiles(null));
        Assert.Null(SteamCmd.StateChangedFiles("garbage"));
    }

    [Fact]
    public void The_state_is_read_off_the_console_line()
    {
        // Issue #15's line. The trailing text after the state must not come along.
        string[] console = ["[2026-08-12 14:42:25] Error! App '2394010' state is 0x6 after update job."];

        Assert.Equal("0x6", SteamCmd.ExtractUpdateState(console, SteamCmd.AppId, RunStart));
    }

    [Fact]
    public void The_newest_state_wins_when_a_run_reports_more_than_one()
    {
        string[] console =
        [
            "[2026-08-12 14:42:25] Error! App '2394010' state is 0x6 after update job.",
            "[2026-08-12 14:42:40] Error! App '2394010' state is 0x202 after update job.",
        ];

        Assert.Equal("0x202", SteamCmd.ExtractUpdateState(console, SteamCmd.AppId, RunStart));
    }

    [Fact]
    public void No_update_job_means_no_state()
    {
        string[] console = ["[2026-08-12 14:42:21] Connecting anonymously to Steam Public..."];

        Assert.Null(SteamCmd.ExtractUpdateState(console, SteamCmd.AppId, RunStart));
    }

    [Theory]
    // Only an unwritable folder withholds it. Low space does not: the repair validates, so it usually fetches
    // nothing, and denying the one cheap fix over a download that probably won't happen is the worse mistake.
    [InlineData(SteamCmdProblem.DiskWrite, false)]
    [InlineData(SteamCmdProblem.DiskSpace, true)]
    [InlineData(SteamCmdProblem.DamagedFiles, true)]
    [InlineData(SteamCmdProblem.Unknown, true)]
    public void A_repair_is_withheld_only_from_a_folder_it_cannot_write_to(SteamCmdProblem problem, bool offered)
    {
        Assert.Equal(offered, Failure(problem: problem).OfferRepair);
    }

    [Fact]
    public void A_repair_is_never_offered_for_a_first_install()
    {
        // Nothing is installed yet, so there is nothing to validate. The dialog's job there is to say what
        // happened.
        Assert.False(Failure(SteamCmdOperation.Install).OfferRepair);
        Assert.True(Failure(SteamCmdOperation.Update).OfferRepair);
    }

    [Fact]
    public void Only_a_failure_with_a_launch_waiting_can_be_started_anyway()
    {
        // Independent of what SteamCMD was doing: the update prompt applies a known-newer build both from a
        // stopped server (no launch) and from the pre-launch check (launch waiting).
        Assert.True(Failure(SteamCmdOperation.Update, launchPending: true).OfferStartAnyway);
        Assert.False(Failure(SteamCmdOperation.Update, launchPending: false).OfferStartAnyway);
    }

    [Theory]
    [InlineData(SteamCmdOperation.Install, "install")]
    [InlineData(SteamCmdOperation.Validate, "verify")]
    [InlineData(SteamCmdOperation.Update, "update")]
    [InlineData(SteamCmdOperation.VerifyOrUpdate, "verify or update")]
    public void The_headline_claims_only_what_was_asked_for(SteamCmdOperation operation, string expected)
    {
        // app_update is the same command every time, so a failure must not claim an update was pending when
        // the launcher was only checking, or when the user pressed Validate Files.
        Assert.Contains(expected, SteamCmdFailureText.Message(Failure(operation)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_validate_is_still_offered_the_repair()
    {
        // Validate Files keeps the app manifest. The repair sets it aside first, so it can resolve the app
        // again where a plain validate would go on trusting a manifest that is wrong.
        Assert.True(Failure(SteamCmdOperation.Validate).OfferRepair);
    }

    [Fact]
    public void The_message_names_SteamCMD_and_carries_its_codes()
    {
        var message = SteamCmdFailureText.Message(Failure(state: "0x6"));

        Assert.Contains("SteamCMD", message);
        Assert.Contains("exit code 8", message);
        Assert.Contains("0x6", message);
    }

    [Fact]
    public void A_run_that_printed_no_state_says_only_the_exit_code()
    {
        var message = SteamCmdFailureText.Message(Failure(state: null));

        Assert.Contains("exit code 8", message);
        Assert.DoesNotContain("state", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_install_and_an_update_are_worded_apart()
    {
        Assert.Contains("install", SteamCmdFailureText.Message(Failure(SteamCmdOperation.Install)));
        Assert.Contains("update", SteamCmdFailureText.Message(Failure(SteamCmdOperation.Update)));
    }

    [Fact]
    public void SteamCMDs_own_lines_go_in_the_detail_box_verbatim()
    {
        var reasons = SteamCmd.ExtractFailureReasons(Issue15, SteamCmd.AppId, RunStart);

        var detail = SteamCmdFailureText.Detail(Failure(reasons: reasons));

        Assert.NotNull(detail);
        Assert.Contains("Failed to get manifest request code, 'Access Denied'", detail);
    }

    [Fact]
    public void No_box_and_a_pointer_to_the_tab_when_SteamCMD_said_nothing()
    {
        var failure = Failure(reasons: []);

        Assert.Null(SteamCmdFailureText.Detail(failure));
        Assert.Contains("SteamCMD tab", SteamCmdFailureText.Message(failure));
    }

    [Fact]
    public void Low_space_is_reported_without_withholding_the_repair()
    {
        var failure = Failure(problem: SteamCmdProblem.DiskSpace, free: "1.2 GB");

        Assert.Contains("1.2 GB", SteamCmdFailureText.Message(failure));
        Assert.Contains(SteamCmdFailureText.Buttons(failure), b => b.Choice == SteamCmdFailureChoice.ValidateAndRetry);
    }

    [Fact]
    public void An_unwritable_folder_is_explained_and_offers_no_repair()
    {
        var failure = Failure(problem: SteamCmdProblem.DiskWrite);

        Assert.Contains("Cannot write", SteamCmdFailureText.Message(failure));
        Assert.DoesNotContain(SteamCmdFailureText.Buttons(failure), b => b.Choice == SteamCmdFailureChoice.ValidateAndRetry);
    }

    [Fact]
    public void The_repair_is_offered_under_SteamCMDs_own_account_not_above_it()
    {
        Assert.Contains("repair the install", SteamCmdFailureText.Footer(Failure())!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("repair the install", SteamCmdFailureText.Message(Failure()), StringComparison.OrdinalIgnoreCase);
        Assert.Null(SteamCmdFailureText.Footer(Failure(SteamCmdOperation.Install)));
    }

    [Fact]
    public void A_run_that_never_reached_the_server_is_not_called_an_update_problem()
    {
        // SteamCMD can fail before it evaluates the app at all: Steam unreachable, no network, its own install
        // broken. Calling that "couldn't update the server" sends the user after the wrong thing.
        var message = SteamCmdFailureText.Message(Failure(reachedApp: false));

        Assert.DoesNotContain("couldn't update", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SteamCmdFailureText.Buttons(Failure(reachedApp: false)),
            b => b.Choice == SteamCmdFailureChoice.ValidateAndRetry);
    }

    [Fact]
    public void Dismissing_a_pre_launch_dialog_falls_back_to_cancel()
    {
        // The View takes the last button when the dialog is closed without one, so declining the start has to
        // sit there rather than a Start anyway the user never clicked.
        var buttons = SteamCmdFailureText.Buttons(Failure(SteamCmdOperation.VerifyOrUpdate));

        Assert.Equal(SteamCmdFailureChoice.Cancel, buttons[^1].Choice);
        Assert.Contains(buttons, b => b.Choice == SteamCmdFailureChoice.Leave);
    }

    [Fact]
    public void Dismissing_an_install_dialog_changes_nothing()
    {
        // No launch waits on the Install button, so Close is the only answer there is.
        var buttons = SteamCmdFailureText.Buttons(Failure(SteamCmdOperation.Install, launchPending: false));

        Assert.Equal(SteamCmdFailureChoice.Leave, Assert.Single(buttons).Choice);
    }

    private static SteamCmdFailure Failure(
        SteamCmdOperation operation = SteamCmdOperation.VerifyOrUpdate,
        bool launchPending = true,
        SteamCmdProblem problem = SteamCmdProblem.Unknown,
        string? state = "0x6",
        bool? changedFiles = true,
        bool reachedApp = true,
        IReadOnlyList<string>? reasons = null,
        double? gb = 5.6,
        string? free = "12 GB") =>
        new(operation, 8, problem, state, changedFiles, reachedApp, launchPending,
            reasons ?? ["AppID 2394010 update canceled : nope"], gb, free);

    [Fact]
    public void A_spent_repair_is_not_offered_again()
    {
        // The user picked Redownload and it failed. Offering it a second time proposes the thing that just
        // didn't work, and launching without asking acts on a choice they never made.
        var failure = Failure(SteamCmdOperation.VerifyOrUpdate) with { RepairFailed = true };
        var buttons = SteamCmdFailureText.Buttons(failure);

        Assert.DoesNotContain(buttons, b => b.Choice == SteamCmdFailureChoice.ValidateAndRetry);
        Assert.Contains(buttons, b => b.Choice == SteamCmdFailureChoice.Leave);
        Assert.Equal(SteamCmdFailureChoice.Cancel, buttons[^1].Choice);
        Assert.Contains("did not fix it", SteamCmdFailureText.Message(failure), StringComparison.OrdinalIgnoreCase);
        Assert.Null(SteamCmdFailureText.Footer(failure));
    }

    [Theory]
    [InlineData(SteamCmdOperation.Install)]
    [InlineData(SteamCmdOperation.Validate)]
    [InlineData(SteamCmdOperation.Update)]
    [InlineData(SteamCmdOperation.VerifyOrUpdate)]
    public void Every_operation_gets_its_own_headline(SteamCmdOperation operation)
    {
        var headlines = Enum.GetValues<SteamCmdOperation>()
            .ToDictionary(op => op, op => SteamCmdFailureText.Message(Failure(op)));

        Assert.Single(headlines, pair => pair.Value == headlines[operation]);
    }

    [Fact]
    public void What_the_caller_asked_for_survives_when_a_server_is_installed()
    {
        // Covers the resolution, not which operation each button passes in. That wiring lives in MainViewModel,
        // which builds its ServerController in its own constructor with no seam to observe the call, and that
        // constructor opens the CLI named pipe. So it is verified by running the app: press Validate Files on a
        // failing install and the dialog must say "verify", not "update". It read "update" for one commit.
        Assert.Equal(SteamCmdOperation.Validate,
            ServerController.ResolveOperation(SteamCmdOperation.Validate, isInstalled: true));
        Assert.Equal(SteamCmdOperation.Update,
            ServerController.ResolveOperation(SteamCmdOperation.Update, isInstalled: true));
    }

    [Theory]
    [InlineData(SteamCmdOperation.Validate)]
    [InlineData(SteamCmdOperation.Update)]
    [InlineData(SteamCmdOperation.VerifyOrUpdate)]
    public void Nothing_can_be_verified_or_updated_before_a_first_install(SteamCmdOperation requested)
    {
        Assert.Equal(SteamCmdOperation.Install, ServerController.ResolveOperation(requested, isInstalled: false));
    }

    [Theory]
    // Only applying a build already known to be newer skips the file check.
    [InlineData(SteamCmdOperation.Update, false)]
    [InlineData(SteamCmdOperation.Install, true)]
    [InlineData(SteamCmdOperation.Validate, true)]
    [InlineData(SteamCmdOperation.VerifyOrUpdate, true)]
    public void Only_a_known_update_skips_validation(SteamCmdOperation operation, bool validates)
    {
        Assert.Equal(validates, ServerController.ValidatesFiles(operation));
    }

    [Fact]
    public void A_run_that_never_reached_the_server_has_nothing_to_ask()
    {
        // No repair applies and there is no decision to make, so the launcher logs it instead of blocking the
        // launch on a modal over what is usually a transient Steam outage.
        var failure = Failure(reachedApp: false);

        Assert.False(failure.OfferRepair);
        Assert.DoesNotContain("couldn't update", SteamCmdFailureText.Message(failure), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_repair_still_offers_the_two_answers_that_remain()
    {
        // Covers the button set after a failed repair, not the gate that decides whether the dialog opens at
        // all. That gate is AskAboutSteamCmdFailure's `answering`, verified by running the app: click Repair,
        // drop the network mid-repair, and a dialog must appear rather than the server launching silently.
        var failure = Failure(reachedApp: false) with { RepairFailed = true };

        Assert.False(failure.OfferRepair);
        Assert.True(failure.OfferStartAnyway);
        Assert.Equal(SteamCmdFailureChoice.Cancel, SteamCmdFailureText.Buttons(failure)[^1].Choice);
    }
}

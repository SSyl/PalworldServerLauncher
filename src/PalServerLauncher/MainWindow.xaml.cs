using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.ViewModels;
using PalServerLauncher.Views;

namespace PalServerLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Logger _logger;
    private readonly bool _startServer;
    private readonly bool _ignoreRestApi;
    private bool _forceClose;

    public MainWindow(Logger logger, LauncherConfig config, bool startServer = false, bool ignoreRestApi = false)
    {
        InitializeComponent();
        _logger = logger;
        _startServer = startServer;
        _ignoreRestApi = ignoreRestApi;
        _viewModel = new MainViewModel(logger, config);
        DataContext = _viewModel;

        // Keep each tab's log scrolled to the newest line as entries are appended.
        HookAutoScroll(_viewModel.LogGeneral, GeneralList);
        HookAutoScroll(_viewModel.LogServer, ServerList);
        HookAutoScroll(_viewModel.LogChat, ChatList);
        HookAutoScroll(_viewModel.LogPlayerJoin, PlayersList);
        HookAutoScroll(_viewModel.LogSteamCmd, SteamCmdList);

        _viewModel.InstallFinished += OnInstallFinished;
        _viewModel.ConfirmInstall = ConfirmInstall;
        _viewModel.RequestShutdownDecision = PromptShutdownDecision;
        _viewModel.ConfirmShutdownNow = ConfirmShutdownNow;

        Loaded += OnLoaded;
        Closing += OnClosing;
        // Dark title bar before the first paint, so the main window doesn't flash a light bar on launch.
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Detect the public IP for the External IP display, in the background (best-effort, never blocks load).
        _ = _viewModel.RefreshPublicIpAsync();

        // Detect an already-running managed server but DON'T adopt it yet: prompt first (reconnect / shut down /
        // exit), then adopt per the choice. Adopting before the prompt would start monitoring a server the user
        // might want gone, and make "Reconnect" a no-op confirming something that already happened.
        if (_viewModel.DetectRunningInstances() > 0 && !await HandleAlreadyRunningAsync())
            return; // the user chose Exit, the launcher is closing

        if (_startServer)
            await AutoStartServerAsync();
        else
            PromptRestSetupIfNeeded();
    }

    /// <summary>--start-server: bring the server up on load. If we adopted an already-running one above, do
    /// nothing. Otherwise auto-configure the REST API (with a random admin password) unless --ignore-rest-api
    /// was passed, then start. StartServerAsync no-ops with a log if the server isn't installed.</summary>
    private async Task AutoStartServerAsync()
    {
        if (_viewModel.IsServerRunning)
            return;

        if (!_ignoreRestApi && _viewModel.ShouldPromptRestSetup())
            _logger.Info(_viewModel.EnableRestApi()
                ? "Auto-enabled the REST API with a random admin password (--start-server)."
                : "Couldn't auto-enable the REST API, starting without it.");

        await _viewModel.StartServerAsync();
    }

    /// <summary>
    /// A managed server was DETECTED at startup (not yet adopted). Only servers launched from THIS folder are
    /// found, so it's almost certainly ours from a previous session, offer to reconnect (adopt and keep managing
    /// it), shut it down, or exit. Adopting happens here, per the choice, so the launcher never starts monitoring
    /// a server the user chose to shut down or leave. Returns false only when the launcher is exiting.
    /// </summary>
    private async Task<bool> HandleAlreadyRunningAsync()
    {
        var count = _viewModel.RunningInstanceCount;

        if (count > 1)
        {
            // Several servers under one folder share a save + ports and will conflict, don't keep them.
            var multiChoice = ChoiceDialog.Show(this, Strings.Main_MultiServerTitle,
                string.Format(Strings.Main_MultiServerMessage, count),
                Strings.Main_ShutDownAll, Strings.Main_ExitLauncher);
            if (multiChoice != 0)
            {
                Application.Current.Shutdown();
                return false;
            }
            _viewModel.Attach(); // adopt the primary so it stops gracefully, then force-stop the rest
            await _viewModel.StopAllInstancesAsync();
            return true;
        }

        // Auto-reconnect (opt-in), or --start-server: a lone running server is almost certainly ours, adopt it
        // silently instead of prompting. Multiple instances always prompt (they conflict).
        if (_viewModel.Config.AutoReconnectSingleInstance || _startServer)
        {
            _viewModel.Attach();
            return true;
        }

        var choice = ChoiceDialog.Show(this, Strings.Main_ServerRunningTitle,
            Strings.Main_ServerRunningMessage,
            Strings.Main_Reconnect, Strings.Main_ShutDownServer, Strings.Main_ExitLauncher);

        switch (choice)
        {
            case 1: // Shut Down Server: adopt, then graceful stop
                _viewModel.Attach();
                await _viewModel.ShutdownGracefulAsync();
                return true;
            case 2: // Exit Launcher: leave it running, don't adopt
                Application.Current.Shutdown();
                return false;
            default: // Reconnect (0) or dismissed (-1): adopt and keep managing it
                _viewModel.Attach();
                return true;
        }
    }

    /// <summary>Confirm the first install (a multi-GB SteamCMD download) before it starts.</summary>
    private bool ConfirmInstall() =>
        ChoiceDialog.Show(this, Strings.Main_InstallTitle,
            Strings.Main_InstallMessage,
            Strings.Main_DownloadInstall, Strings.Common_Cancel) == 0;

    /// <summary>Import an existing (non-launcher) server: browse to it, validate, confirm the copy, then copy it in.
    /// On success the VM fires InstallFinished, which offers REST setup like a fresh install.</summary>
    private async void OnImportServer(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Strings.Main_ImportPickTitle };
        if (picker.ShowDialog(this) != true)
            return;

        var source = picker.FolderName;
        if (!ServerImporter.LooksLikeServerInstall(source))
        {
            ChoiceDialog.Show(this, Strings.Main_ImportTitle, Strings.Main_ImportInvalidMessage, Strings.Common_OK);
            return;
        }

        if (ChoiceDialog.Show(this, Strings.Main_ImportTitle,
                string.Format(Strings.Main_ImportConfirmMessage, source),
                Strings.Main_ImportCopyButton, Strings.Common_Cancel) != 0)
            return;

        var imported = await _viewModel.ImportServerAsync(source);
        if (!imported)
            ChoiceDialog.Show(this, Strings.Main_ImportTitle, Strings.Main_ImportFailedMessage, Strings.Common_OK);
    }

    /// <summary>Confirm skipping a timed shutdown's countdown to shut the server down immediately.</summary>
    private bool ConfirmShutdownNow() =>
        ChoiceDialog.Show(this, Strings.Main_ShutdownNowTitle,
            Strings.Main_ShutdownNowMessage,
            Strings.Main_ShutDownNow, Strings.Main_KeepWaiting) == 0;

    /// <summary>The Stop-button shutdown prompt: immediate / timed when REST is on, or a force-stop notice when
    /// it's off. Returns the user's choice, the ViewModel routes it. The dialogs live here, never in the VM.</summary>
    private ShutdownDecision PromptShutdownDecision()
    {
        if (!_viewModel.IsRestApiReady)
        {
            var forceChoice = ChoiceDialog.Show(this, Strings.Main_ShutdownTitle,
                Strings.Main_ShutdownForceMessage,
                Strings.Main_ForceStop, Strings.Common_Cancel);
            return new ShutdownDecision(forceChoice == 0 ? ShutdownKind.ForceNoRest : ShutdownKind.Cancel);
        }

        var choice = ChoiceDialog.Show(this, Strings.Main_ShutdownTitle,
            Strings.Main_ShutdownChoiceMessage,
            Strings.Main_ShutdownNowButton, Strings.Main_TimedShutdown, Strings.Common_Cancel);
        if (choice == 0)
            return new ShutdownDecision(ShutdownKind.GracefulNow);
        if (choice == 1)
        {
            var seconds = NumberPromptDialog.Show(this, Strings.Main_TimedShutdownTitle,
                Strings.Main_TimedShutdownMessage,
                Strings.Main_SecondsUnit, defaultValue: 60, min: 1, max: 3600);
            return seconds is int s ? new ShutdownDecision(ShutdownKind.Timed, s) : new ShutdownDecision(ShutdownKind.Cancel);
        }
        return new ShutdownDecision(ShutdownKind.Cancel);
    }

    /// <summary>Right after a fresh install, offer to enable the REST API (with install-specific wording).</summary>
    private void OnInstallFinished() => PromptRestSetupIfNeeded(afterInstall: true);

    /// <summary>
    /// Encourage enabling the REST API while it's off (with a random admin password). Two entry points:
    /// at startup on an existing REST-off server, and right after a fresh install (<paramref name="afterInstall"/>).
    /// </summary>
    private void PromptRestSetupIfNeeded(bool afterInstall = false)
    {
        if (!_viewModel.ShouldPromptRestSetup())
            return;

        if (afterInstall)
        {
            var installChoice = ChoiceDialog.Show(this, Strings.Main_ServerInstalledTitle,
                Strings.Main_ServerInstalledMessage,
                Strings.Main_YesEnableRest, Strings.Main_NotNow);
            if (installChoice == 0)
                EnableRestAndReport();
            return;
        }

        var choice = ChoiceDialog.Show(this, Strings.Main_EnableRestTitle,
            Strings.Main_EnableRestMessage,
            Strings.Main_YesEnableRest, Strings.Main_NoRunLimited, Strings.Main_Exit);

        if (choice == 0)
            EnableRestAndReport();
        else if (choice == 2)
            Application.Current.Shutdown();
        // "No, run limited" (1) or dismissed (-1): run limited this session; we ask again next launch.
    }

    /// <summary>Enable the REST API with a fresh random password and report the outcome.</summary>
    private void EnableRestAndReport()
    {
        var ok = _viewModel.EnableRestApi();
        ChoiceDialog.Show(this,
            ok ? Strings.Main_RestEnabledTitle : Strings.Main_RestFailedTitle,
            ok ? Strings.Main_RestEnabledMessage : Strings.Main_RestFailedMessage,
            Strings.Common_OK);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || !_viewModel.IsServerRunning)
            return;

        e.Cancel = true; // keep the window open until the user decides
        var choice = ChoiceDialog.Show(this, Strings.Main_ServerStillRunningTitle,
            Strings.Main_ServerStillRunningMessage,
            Strings.Main_ShutDownGraceful, Strings.Common_ForceShutdown, Strings.Main_LeaveRunning);

        switch (choice)
        {
            case 0: _ = ShutdownThenClose(_viewModel.ShutdownGracefulAsync()); break;
            case 1: _ = ShutdownThenClose(_viewModel.ForceStopAsync()); break;
            // Leave the server running (orphaned by choice). Just let THIS close proceed, calling Close()
            // here throws because we're already inside the Closing handler (WPF forbids it mid-close).
            case 2: e.Cancel = false; break;
            // default (-1): dialog dismissed -> stay open
        }
    }

    private async Task ShutdownThenClose(Task shutdown)
    {
        await shutdown;
        _forceClose = true;
        Close();
    }

    private void OnEditRestartTimes(object sender, RoutedEventArgs e)
    {
        var result = RestartTimesDialog.Show(this, _viewModel.RestartTimes, Strings.Times_RestartTitle, Strings.Times_RestartHeader);
        if (result is not null)
            _viewModel.SetRestartTimes(result);
    }

    private void OnEditBackupTimes(object sender, RoutedEventArgs e)
    {
        var result = RestartTimesDialog.Show(this, _viewModel.BackupTimes, Strings.Times_BackupTitle, Strings.Times_BackupHeader);
        if (result is not null)
            _viewModel.SetBackupTimes(result);
    }

    private void OnOpenLauncherSettings(object sender, RoutedEventArgs e)
    {
        if (LauncherSettingsDialog.Show(this, _viewModel.Config))
            PromptRestartForLanguage();
    }

    /// <summary>After a language change, offer to restart the launcher now to apply it. A running server keeps
    /// running and the fresh instance re-adopts it, so this mirrors the "leave running" close path.</summary>
    private void PromptRestartForLanguage()
    {
        var language = LauncherLanguages.ForCode(_viewModel.Config.Language).DisplayName;
        var message = _viewModel.IsServerRunning
            ? string.Format(Strings.LauncherSettings_RestartPromptRunning, language)
            : string.Format(Strings.LauncherSettings_RestartPrompt, language);
        if (ChoiceDialog.Show(this, Strings.LauncherSettings_Title, message,
                Strings.LauncherSettings_RestartNow, Strings.Main_NotNow) == 0)
            RestartApp();
    }

    /// <summary>Relaunch the launcher: start a fresh instance (preserving the original CLI args), then exit
    /// this one without disturbing a running server, which the new instance re-adopts. Aborts if the new
    /// process can't be started, leaving the change to apply on the next manual launch.</summary>
    private void RestartApp()
    {
        var exePath = Environment.ProcessPath;
        if (exePath is null)
            return;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(exePath) { UseShellExecute = false };
            var args = Environment.GetCommandLineArgs();
            for (var i = 1; i < args.Length; i++) // preserve --debug / --console etc, skip the exe path
                start.ArgumentList.Add(args[i]);
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex)
        {
            _logger.Error("Couldn't restart the launcher to apply the language", ex);
            return;
        }
        _forceClose = true;
        Application.Current.Shutdown();
    }

    private void OnOpenServerSettings(object sender, RoutedEventArgs e) =>
        SettingsDialog.ShowServerSettings(this, _viewModel.Config, _viewModel.GameSettings, _viewModel.IsServerRunning);

    private void OnOpenMods(object sender, RoutedEventArgs e)
    {
        if (ModsDialog.Show(this, _viewModel.Config, _viewModel.ModService, _viewModel.ConnectSteamAsync, _viewModel.CheckSteamLoginAsync))
            _viewModel.ApplyModSettings();
    }

    private void OnEditAnnouncements(object sender, RoutedEventArgs e) =>
        AnnouncementsDialog.Show(this, _viewModel.Config);

    private void OnOpenDiscord(object sender, RoutedEventArgs e)
    {
        if (DiscordDialog.Show(this, _viewModel.Config))
            _viewModel.ApplyDiscordSettings();
    }

    private void OnOpenServerCommands(object sender, RoutedEventArgs e) =>
        ServerCommandsDialog.Show(this, _viewModel.ServerCommands, _logger);

    private void OnForceShutdown(object sender, RoutedEventArgs e)
    {
        if (ChoiceDialog.Show(this, Strings.Common_ForceShutdown,
                Strings.Main_ForceShutdownConfirmMessage, Strings.Common_ForceShutdown, Strings.Common_Cancel) != 0)
            return;
        _viewModel.ForceShutdownNow();
    }

    private void OnCheckPorts(object sender, RoutedEventArgs e)
    {
        var consent = ChoiceDialog.Show(this, Strings.Main_PortCheck,
            Strings.Main_PortCheckConsentMessage,
            Strings.Common_OK, Strings.Common_Cancel);
        if (consent != 0)
            return;

        PortCheckDialog.Show(this, _viewModel.Config, _viewModel.ReadServerSettings(), _viewModel.PublicIp, _logger);
    }

    private void OnToggleIpReveal(object sender, RoutedEventArgs e) =>
        _viewModel.IsIpRevealed = !_viewModel.IsIpRevealed;

    private void OnCopyConnectionInfo(object sender, RoutedEventArgs e)
    {
        var info = _viewModel.ConnectionInfo;
        if (string.IsNullOrEmpty(info))
            return;
        try { Clipboard.SetText(info); }
        catch (System.Runtime.InteropServices.COMException) { /* clipboard busy - ignore */ }
    }

    // Check for Update needs a dialog (the download prompt), so it's orchestrated here in the View.
    private async void OnCheckForUpdate(object sender, RoutedEventArgs e)
    {
        var (result, latest) = await _viewModel.CheckForUpdateAsync();
        if (result != ServerController.UpdateCheckResult.UpdateAvailable)
            return; // the Update status line already shows up-to-date / check-failed

        if (_viewModel.IsServerRunning)
        {
            var choice = ChoiceDialog.Show(this, Strings.Main_UpdateAvailableTitle,
                string.Format(Strings.Main_UpdateRunningMessage, latest),
                Strings.Main_UpdateRestart, Strings.Main_NotNow);
            if (choice == 0)
                await _viewModel.UpdateAndRestartAsync();
        }
        else
        {
            var choice = ChoiceDialog.Show(this, Strings.Main_UpdateAvailableTitle,
                string.Format(Strings.Main_UpdateStoppedMessage, latest),
                Strings.Main_Download, Strings.Main_NotNow);
            if (choice == 0)
                await _viewModel.DownloadUpdateAsync();
        }
    }

    // Digit-only gating for the announce lead-minute boxes (typing + paste).
    private void OnDigitsOnly(object sender, TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
            if (!char.IsAsciiDigit(c)) { e.Handled = true; return; }
    }

    private void OnLeadPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is string s)
            foreach (var c in s)
                if (!char.IsAsciiDigit(c)) { e.CancelCommand(); return; }
    }

    private void HookAutoScroll(ObservableCollection<string> collection, ListBox list)
    {
        collection.CollectionChanged += (_, e) =>
        {
            if (e.Action != NotifyCollectionChangedAction.Add)
                return;

            // Defer the scroll: calling ScrollIntoView synchronously inside CollectionChanged forces a
            // layout pass mid-notification, which under rapid streaming adds throws
            // "ItemsControl is inconsistent with its items source". Background priority lets it settle.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (list.Items.Count > 0)
                    list.ScrollIntoView(list.Items[^1]);
            }));
        };
    }
}

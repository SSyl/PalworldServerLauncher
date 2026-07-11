using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PalServerLauncher.Core;
using PalServerLauncher.Logging;
using PalServerLauncher.ViewModels;
using PalServerLauncher.Views;

namespace PalServerLauncher;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly Logger _logger;
    private bool _forceClose;

    public MainWindow(Logger logger)
    {
        InitializeComponent();
        _logger = logger;
        _viewModel = new MainViewModel(logger);
        DataContext = _viewModel;

        // Keep each tab's log scrolled to the newest line as entries are appended.
        HookAutoScroll(_viewModel.LogGeneral, GeneralList);
        HookAutoScroll(_viewModel.LogServer, ServerList);
        HookAutoScroll(_viewModel.LogSteamCmd, SteamCmdList);

        _viewModel.InstallFinished += OnInstallFinished;
        _viewModel.ConfirmInstall = ConfirmInstall;

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Detect the public IP for the External IP display, in the background (best-effort, never blocks load).
        _ = _viewModel.RefreshPublicIpAsync();

        // Attach() adopts an already-running managed server; if one was found, offer to keep, stop, or exit.
        if (_viewModel.Attach() && !await HandleAlreadyRunningAsync())
            return; // the user chose Exit - the launcher is closing

        PromptRestSetupIfNeeded();
    }

    /// <summary>
    /// A managed server was found at startup and adopted. Only servers launched from THIS folder are
    /// detected, so it's almost certainly ours from a previous session - offer to reconnect (keep managing
    /// it), shut it down, or exit. Returns false only when the launcher is exiting.
    /// </summary>
    private async Task<bool> HandleAlreadyRunningAsync()
    {
        var count = _viewModel.RunningInstanceCount;

        if (count > 1)
        {
            // Several servers under one folder share a save + ports and will conflict - don't keep them.
            var multiChoice = ChoiceDialog.Show(this, "Multiple servers running",
                $"Detected {count} Palworld servers running from this folder. They share one save and set of " +
                "ports, so they'll conflict - the launcher manages a single server. Shut them all down, or exit.",
                "Shut Down All", "Exit Launcher");
            if (multiChoice != 0)
            {
                Application.Current.Shutdown();
                return false;
            }
            await _viewModel.StopAllInstancesAsync();
            return true;
        }

        var choice = ChoiceDialog.Show(this, "Server already running",
            "A Palworld server started from this folder is already running - almost certainly yours from a " +
            "previous session, since the launcher only tracks servers it started here.\n\n" +
            "Reconnect to keep managing it over the REST API (live stats, scheduled restarts, backups; the " +
            "Server Log tab stays empty for a server the launcher didn't start itself). Otherwise shut it down, or exit.",
            "Reconnect", "Shut Down Server", "Exit Launcher");

        switch (choice)
        {
            case 1: // Shut Down Server
                await _viewModel.ShutdownGracefulAsync();
                return true;
            case 2: // Exit Launcher
                Application.Current.Shutdown();
                return false;
            default: // Reconnect (0) or dismissed (-1): keep the adopted server and stay open
                return true;
        }
    }

    /// <summary>Confirm the first install (a multi-GB SteamCMD download) before it starts.</summary>
    private bool ConfirmInstall() =>
        ChoiceDialog.Show(this, "Install the server?",
            "This downloads and installs the Palworld dedicated server with SteamCMD - about 4 GB - into this " +
            "launcher's folder. A SteamCMD window opens to show progress.\n\nDownload and install it now?",
            "Download & Install", "Cancel") == 0;

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
            var installChoice = ChoiceDialog.Show(this, "Server installed",
                "Server successfully installed.\n\n" +
                "Enable the REST API? It's what lets the launcher control the server: apply game updates, " +
                "gracefully save and shut down, schedule restarts and backups, and monitor server health.\n\n" +
                "If you enable it, the launcher generates the server config now and sets a secure, randomly " +
                "generated admin password (view or change it later under Server Settings, in the Admin tab). You " +
                "can also skip this and enable it yourself later.",
                "Yes, enable it", "Not now");
            if (installChoice == 0)
                EnableRestAndReport();
            return;
        }

        var choice = ChoiceDialog.Show(this, "Enable the REST API?",
            "This launcher is built around Palworld's REST API and uses it for almost everything: graceful " +
            "save-and-shutdown, fresh backups, live health / zombie monitoring, automatic restarts, and player " +
            "join/leave logging.\n\n" +
            "The REST API isn't enabled on this server yet, so those features will be limited - shutdowns fall " +
            "back to a hard kill, backups may be stale, and there are no automatic restarts.\n\n" +
            "Enable it now with a secure, randomly-generated admin password?",
            "Yes, enable it", "No, run limited", "Exit");

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
            ok ? "REST API enabled" : "Couldn't enable REST",
            ok
                ? "Done - the REST API is on with a new random admin password (view or change it under Server " +
                  "Settings, in the Admin tab). It takes effect the next time you start the server."
                : "Couldn't write the server config. Make sure the server is installed and stopped, then set " +
                  "it manually under Server Settings, in the Admin tab.",
            "OK");
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_forceClose || !_viewModel.IsServerRunning)
            return;

        e.Cancel = true; // keep the window open until the user decides
        var choice = ChoiceDialog.Show(this, "Server still running",
            "The Palworld server is still running. What should happen to it?",
            "Shut Down (graceful)", "Force Stop", "Leave Running");

        switch (choice)
        {
            case 0: _ = ShutdownThenClose(_viewModel.ShutdownGracefulAsync()); break;
            case 1: _ = ShutdownThenClose(_viewModel.ForceStopAsync()); break;
            // Leave the server running (orphaned by choice). Just let THIS close proceed - calling Close()
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
        var result = RestartTimesDialog.Show(this, _viewModel.RestartTimes);
        if (result is not null)
            _viewModel.SetRestartTimes(result);
    }

    private void OnEditBackupTimes(object sender, RoutedEventArgs e)
    {
        var result = RestartTimesDialog.Show(this, _viewModel.BackupTimes, "Backup");
        if (result is not null)
            _viewModel.SetBackupTimes(result);
    }

    private void OnOpenServerSettings(object sender, RoutedEventArgs e) =>
        SettingsDialog.ShowServerSettings(this, _viewModel.Config, _viewModel.GameSettings, _viewModel.IsServerRunning);

    private void OnOpenAdvanced(object sender, RoutedEventArgs e)
    {
        var proceed = ChoiceDialog.Show(this, "Danger zone!",
            "These are advanced, low-level settings. Using the wrong values here can HURT performance - for "
            + "example, setting the process priority too high can starve the rest of your system and make the "
            + "server run worse, not better.\n\nDon't use these unless you know what you're doing.",
            "I understand, continue", "Cancel");
        if (proceed == 0)
            SettingsDialog.ShowAdvanced(this, _viewModel.Config, _viewModel.GameSettings, _viewModel.IsServerRunning);
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

    private void OnCheckPorts(object sender, RoutedEventArgs e)
    {
        var consent = ChoiceDialog.Show(this, "Port Accessibility",
            "Your public IP address will be sent to check-host.cc, a free external service, so it can probe your "
            + "ports from the internet. Nothing except your public IP and the ports you test is sent to their service.",
            "OK", "Cancel");
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
            var choice = ChoiceDialog.Show(this, "Update available",
                $"A newer server build ({latest}) is available. Update and restart the server now? Players get the usual restart warnings.",
                "Update & restart", "Not now");
            if (choice == 0)
                await _viewModel.UpdateAndRestartAsync();
        }
        else
        {
            var choice = ChoiceDialog.Show(this, "Update available",
                $"A newer server build ({latest}) is available. Download and apply it now?",
                "Download", "Not now");
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

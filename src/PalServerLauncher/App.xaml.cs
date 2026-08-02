using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest;
using PalServerLauncher.Views;

namespace PalServerLauncher;

public partial class App : Application
{
    private Logger _logger = null!;

    // Throttle repeated Discord reconnect failures: an outage can surface many identical unobserved exceptions
    // in a single garbage-collection burst, so log the first and suppress the same one for a few minutes.
    private string? _lastDiscordNoise;
    private DateTime _lastDiscordNoiseUtc;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool HasFlag(params string[] names) =>
            e.Args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

        var verbose = HasFlag("--debug", "--verbose", "-debug", "-verbose", "-v");

        // --install-server runs a headless SteamCMD install/update to completion, then exits, no window. It
        // implies a console so the progress is visible.
        var installServer = HasFlag("--install-server", "-install-server");

        // --start-server opens the GUI as usual and auto-starts (or adopts) the server once loaded, and
        // auto-configures the REST API with a random admin password unless --ignore-rest-api is also passed.
        var startServer = HasFlag("--start-server", "-start-server");
        var ignoreRestApi = HasFlag("--ignore-rest-api", "-ignore-rest-api");

        // --stop-server [seconds] / --kill-server stop the server without opening a window. Like --install-server
        // they imply a console so the outcome is visible.
        var stopRequest = StopRequest.FromCommandLine(e.Args, out var stopError);
        var stopping = stopRequest is not null || stopError is not null;

        // --console mirrors all log output to a terminal (the launching one, or a new window), so the
        // launcher can be watched from the command line. The GUI window still opens.
        var console = (HasFlag("--console", "-console", "-c") || installServer || stopping) && ConsoleBridge.Enable();

        // Move any legacy data (settings/steamcmd/server/backups/logs sitting next to the exe) into the
        // PalworldServerLauncher data folder before logging starts, so the exe ends up alone.
        var migrated = LauncherConfig.MigrateLegacyLayout();

        _logger = new Logger(verbose, echoToConsole: console);
        if (migrated.Count > 0)
            _logger.Info($"Moved existing data into {LauncherConfig.DataRoot}: {string.Join(", ", migrated)}");

        // Without these, any unhandled exception (e.g. inside an async command) silently closes the window.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) _logger.Error("Unhandled AppDomain exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Discord.Net's reconnect loop throws transient connect/HTTP failures (e.g. a Discord 500) into
            // background tasks that surface here. They self-heal on retry, so log one concise line instead of a
            // full ERROR stack trace per attempt. A genuine bad-token failure is caught in DiscordBotService.
            var discordInner = DiscordNoiseFilter.FindConnectionNoise(args.Exception);

            if (discordInner is not null)
            {
                var msg = discordInner.Message;
                if (msg != _lastDiscordNoise || DateTime.UtcNow - _lastDiscordNoiseUtc > TimeSpan.FromMinutes(5))
                {
                    _lastDiscordNoise = msg;
                    _lastDiscordNoiseUtc = DateTime.UtcNow;
                    _logger.Info($"Discord bot connection error, will keep retrying: {msg}");
                }
            }
            else
                _logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Headless stop, before the install branch: an install refuses to run while a server is up anyway.
        if (stopError is not null)
        {
            _logger.Error(stopError);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Dispatcher.BeginInvoke(() => Shutdown(1));
            return;
        }

        if (stopRequest is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunHeadlessStopAsync(stopRequest, LauncherConfig.Load());
            return;
        }

        // Headless install: run the SteamCMD install/update to completion, then exit with a code. No window,
        // no schedulers or Discord (we drive SteamCMD directly, not the full controller).
        if (installServer)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _ = RunHeadlessInstallAsync(LauncherConfig.Load());
            return;
        }

        // Paint every window's OS title bar dark to match the app (WPF leaves it light by default). A class
        // handler on Loaded covers every window, current and future, including the code-built dialogs.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) DarkTitleBar.Apply(w); }));

        // The single-instance prompt and the first-run language picker are both shown before MainWindow exists,
        // so each is briefly the only open window. Under the default OnLastWindowClose, closing one would put the
        // app into shutting-down state before MainWindow opens, and every later ShutdownMode set then throws.
        // Hold shutdown from here until MainWindow is up, then restore the normal close-to-exit behavior.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Only one GUI per install folder. Reached after the headless branches on purpose: --stop-server and
        // --install-server ARE second instances by design and must never trip this.
        if (!EnsureOnlyLauncherForThisFolder())
        {
            Shutdown(1);
            return;
        }

        // Load the config once here (not in MainViewModel) so the UI language is set before MainWindow's
        // XAML is evaluated, and thread the same instance through the window and view model. On a fresh
        // install (no launcher.json yet) let the user pick a language first, defaulting to English. The
        // dark-title-bar handler is registered above, so the picker gets a dark title bar too.
        var freshInstall = !File.Exists(LauncherConfig.DefaultPath);
        var config = LauncherConfig.Load();
        if (freshInstall)
        {
            config.Language = LanguagePickerDialog.Show(config.Language);
            config.Save();
        }
        ApplyUiCulture(config.Language);

        new MainWindow(_logger, config, startServer, ignoreRestApi).Show();
        ShutdownMode = ShutdownMode.OnLastWindowClose;
    }

    /// <summary>Headless install/update: fetch SteamCMD if missing, run the install/update to completion with
    /// progress on the attached console, then shut down with an exit code (0 = success). Refuses if a managed
    /// server is already running (its files would be locked).</summary>
    private async Task RunHeadlessInstallAsync(LauncherConfig config)
    {
        var exitCode = 1;
        try
        {
            var running = ProcessScanner.FindManagedServer(config.ServerRoot);
            if (running is not null)
            {
                running.Dispose();
                _logger.Error("A managed server is already running. Stop it before installing or updating.");
            }
            else
            {
                var steam = new SteamCmd(config.ServerRoot);
                var log = new Progress<string>(_logger.SteamCmd);
                using var tail = new FileTailer(steam.ConsoleLogPath, _logger.SteamCmd, fromStart: false);
                _logger.Info("Installing / updating the Palworld dedicated server (headless)...");
                await steam.EnsureSteamCmdAsync(log, visible: false).ConfigureAwait(false);
                var exit = await steam.InstallOrUpdateServerAsync(validate: true, visible: false, log).ConfigureAwait(false);
                if (exit == 0)
                {
                    _logger.Info($"Install / update complete (build {steam.ReadInstalledBuildId() ?? "?"}).");
                    exitCode = 0;
                }
                else
                {
                    _logger.Error($"SteamCMD exited with code {exit}.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Headless install failed", ex);
        }
        Dispatcher.Invoke(() => Shutdown(exitCode));
    }

    /// <summary>
    /// Two launchers on one folder share a server, a config, and a data root, and would fight over restarts,
    /// backups, and updates. Two launchers in DIFFERENT folders are a supported setup (one install each), so this
    /// matches on the exe path, never on the process name. Returns false when this instance should not start.
    /// </summary>
    private bool EnsureOnlyLauncherForThisFolder()
    {
        var others = ProcessScanner.FindSiblingLaunchers();
        if (others.Count == 0)
            return true;

        try
        {
            _logger.Info($"Another launcher is already running from this folder (PID {string.Join(", ", others.Select(p => p.Id))}).");

            // No owner window: MainWindow does not exist yet, and this has to be settled before anything
            // (schedulers, the CLI listener, server adoption) starts up behind it.
            var choice = ChoiceDialog.Show(null, Strings.SingleInstance_Title, Strings.SingleInstance_Message,
                Strings.SingleInstance_ForceClose, Strings.Main_ExitLauncher);

            if (choice != 0)
                return false;

            foreach (var other in others)
            {
                try
                {
                    // false is load-bearing: the server IS a child of the launcher that started it (no Job Object,
                    // but still a child), so entireProcessTree: true would take the running server down with it.
                    other.Kill(entireProcessTree: false);
                    other.WaitForExit(5000);
                    _logger.Info($"Force closed the other launcher (PID {other.Id}).");
                }
                catch (Exception ex)
                {
                    _logger.Error($"Couldn't close the other launcher (PID {other.Id})", ex);
                    return false;
                }
            }
            return true;
        }
        finally
        {
            foreach (var other in others)
                other.Dispose();
        }
    }

    /// <summary>
    /// Headless stop. Asks a running launcher first, over its named pipe, so ITS stop path runs: an external kill
    /// would fire ServerController.OnProcessExited with no manual stop latched, which reads as a crash and
    /// relaunches the server. Only when no launcher answers do we stop the server ourselves.
    /// </summary>
    private async Task RunHeadlessStopAsync(StopRequest request, LauncherConfig config)
    {
        var exitCode = 1;
        try
        {
            // A graceful stop mirrors the launcher's log here, which usually ends on the same line as the outcome
            // ("Server stopped."). Track the last one so we don't print it twice.
            string? lastForwarded = null;
            var outcome = await LauncherIpcClient
                .SendAsync(LauncherIpc.PipeNameFor(LauncherConfig.DataRoot), request,
                    line => { lastForwarded = line; _logger.Info(line); })
                .ConfigureAwait(false);

            if (outcome is null)
            {
                _logger.Info("No running launcher answered, stopping the server directly.");
                exitCode = await StopServerDirectlyAsync(request, config).ConfigureAwait(false) ? 0 : 1;
            }
            else if (outcome.Succeeded)
            {
                if (outcome.Message != lastForwarded)
                    _logger.Info(outcome.Message);
                exitCode = 0;
            }
            else
            {
                _logger.Error(outcome.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Headless stop failed", ex);
        }
        Dispatcher.Invoke(() => Shutdown(exitCode));
    }

    /// <summary>
    /// Stop the server with no launcher involved: the same ladder as ServerController.StopCoreAsync (shutdown
    /// backup or save, REST shutdown, kill if it drags), without the monitors and state machine. A countdown is
    /// only started here, not waited out, matching what a running launcher reports back for one. One deliberate
    /// difference: a REFUSED /shutdown stops here rather than falling through to a kill. In the launcher that
    /// fallback is safe because it owns the process, whereas a bare CLI killing a server whose REST just said no
    /// is more likely to be losing someone's progress than fixing anything.
    /// </summary>
    private async Task<bool> StopServerDirectlyAsync(StopRequest request, LauncherConfig config)
    {
        using var process = ProcessScanner.FindManagedServer(config.ServerRoot);
        if (process is null)
        {
            _logger.Info("No server is running.");
            return true; // nothing to stop is a success, so a scripted stop is idempotent
        }

        // Checked only once there IS something to stop, so "nothing was running" keeps its documented exit 0.
        // A launcher can be alive with no listener (it lost the pipe name, or never claimed it) and SendAsync
        // cannot tell that from nobody being home. Stopping the server behind that launcher is read as a crash
        // and relaunched, the whole failure this feature exists to prevent, so refuse instead.
        var siblings = ProcessScanner.FindSiblingLaunchers();
        try
        {
            if (siblings.Count > 0)
            {
                _logger.Error($"A launcher is running from this folder (PID {string.Join(", ", siblings.Select(p => p.Id))}) " +
                              "but did not answer, so it isn't safe to stop the server behind it. Close that launcher, or use its Stop button.");
                return false;
            }
        }
        finally
        {
            foreach (var sibling in siblings)
                sibling.Dispose();
        }

        if (request.Kind == StopKind.Kill)
        {
            _logger.Info($"Killing the server process (PID {process.Id}).");
            process.Kill(entireProcessTree: true);
            return await ServerController.WaitForExitAsync(process, TimeSpan.FromSeconds(10), CancellationToken.None)
                .ConfigureAwait(false);
        }

        var settings = IniReader.ReadFile(ServerController.PalWorldSettingsPathFor(config.ServerRoot));
        if (!settings.RestApiUsable)
        {
            _logger.Error("REST API is off, so the server can't be saved or shut down cleanly. Use --kill-server to force it down.");
            return false;
        }

        using var rest = new PalworldRestClient(settings.RestApiPortOrDefault, settings.AdminPassword!);
        var wait = ServerController.ShutdownWaitSeconds(request.Kind == StopKind.Countdown ? request.Seconds : 0);

        if (config.BackupOnShutdown)
            await new BackupService(config, _logger).BackupNowAsync(BackupReason.Shutdown, rest, serverRunning: true).ConfigureAwait(false);
        else
            await rest.SaveAsync().ConfigureAwait(false);

        _logger.Info($"Shutting down (wait {wait}s)...");
        if (!await rest.ShutdownAsync(wait, "Server is shutting down.").ConfigureAwait(false))
        {
            // Unlike the launcher's ladder, nothing follows this, so the server really is still up.
            _logger.Error("The server rejected the shutdown request. It is still running. Use --kill-server to force it down.");
            return false;
        }

        if (request.Kind == StopKind.Countdown)
        {
            _logger.Info($"Shutting down in {wait}s.");
            return true;
        }

        if (await ServerController.WaitForExitAsync(process, TimeSpan.FromSeconds(wait + 30), CancellationToken.None).ConfigureAwait(false))
        {
            _logger.Info("Server stopped.");
            return true;
        }

        _logger.Info("Graceful shutdown timed out, killing the process.");
        process.Kill(entireProcessTree: true);
        return await ServerController.WaitForExitAsync(process, TimeSpan.FromSeconds(10), CancellationToken.None)
            .ConfigureAwait(false);
    }

    // Set only the UI culture (drives resx lookup). CurrentCulture is left on the OS regional setting so
    // number and date formatting (e.g. the restart-times 12/24h clock) is unaffected by the UI language.
    private static void ApplyUiCulture(string language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language);
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
            // Unknown/blank tag: leave the default UI culture, which resolves to the neutral English strings.
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger.Error("Unhandled UI exception", e.Exception);
        MessageBox.Show(
            string.Format(Strings.App_ErrorFormat, e.Exception.Message, _logger.FilePath),
            Strings.Common_AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the window alive
    }
}

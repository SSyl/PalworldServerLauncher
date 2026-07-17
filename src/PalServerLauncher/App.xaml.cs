using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
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

        // --console mirrors all log output to a terminal (the launching one, or a new window), so the
        // launcher can be watched from the command line. The GUI window still opens.
        var console = (HasFlag("--console", "-console", "-c") || installServer) && ConsoleBridge.Enable();

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

        // The first-run language picker is shown before MainWindow exists, so it is briefly the only open
        // window. Under the default OnLastWindowClose, closing it would exit the app before MainWindow opens.
        // Hold shutdown until MainWindow is up, then restore the normal close-to-exit behavior.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

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

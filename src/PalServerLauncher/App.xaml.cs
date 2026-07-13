using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;

namespace PalServerLauncher;

public partial class App : Application
{
    private Logger _logger = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool HasFlag(params string[] names) =>
            e.Args.Any(a => names.Any(n => a.Equals(n, StringComparison.OrdinalIgnoreCase)));

        var verbose = HasFlag("--debug", "--verbose", "-debug", "-verbose", "-v");

        // --console mirrors all log output to a terminal (the launching one, or a new window), so the
        // launcher can be watched from the command line. The GUI window still opens.
        var console = HasFlag("--console", "-console", "-c") && ConsoleBridge.Enable();

        // Move any legacy data (settings/steamcmd/server/backups/logs sitting next to the exe) into the
        // PalworldServerLauncher data folder before logging starts, so the exe ends up alone.
        var migrated = LauncherConfig.MigrateLegacyLayout();

        _logger = new Logger(verbose, echoToConsole: console);
        if (migrated.Count > 0)
            _logger.Info($"Moved existing data into {LauncherConfig.DataRoot}: {string.Join(", ", migrated)}");

        // Load the config once here (not in MainViewModel) so the UI language is set before MainWindow's
        // XAML is evaluated, and thread the same instance through the window and view model.
        var config = LauncherConfig.Load();
        ApplyUiCulture(config.Language);

        // Without these, any unhandled exception (e.g. inside an async command) silently closes the window.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) _logger.Error("Unhandled AppDomain exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            _logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Paint every window's OS title bar dark to match the app (WPF leaves it light by default). A class
        // handler on Loaded covers every window, current and future, including the code-built dialogs.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => { if (s is Window w) DarkTitleBar.Apply(w); }));

        new MainWindow(_logger, config).Show();
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
            $"An error occurred:\n\n{e.Exception.Message}\n\nFull details in:\n{_logger.FilePath}",
            Strings.Common_AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true; // keep the window alive
    }
}

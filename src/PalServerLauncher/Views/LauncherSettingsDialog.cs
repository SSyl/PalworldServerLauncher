using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Launcher-level preferences (UI language, single-instance auto-reconnect, and login autostart). A dark modal
/// built in code, mirroring the other one-off dialogs. Language and auto-reconnect persist on Save, the login
/// autostart Startup shortcut applies immediately on click. Returns true if the language changed, so the caller
/// can restart the launcher to apply it (restart-to-apply, not a live switch).
/// </summary>
public sealed class LauncherSettingsDialog : Window
{
    private readonly LauncherConfig _config;
    private readonly ComboBox _languages;
    private readonly CheckBox _autoReconnect;
    private readonly CheckBox _loginOpen;
    private readonly CheckBox _hideSteamCmd;
    private readonly CheckBox _logHealthStats;
    private bool _changed;

    private LauncherSettingsDialog(LauncherConfig config)
    {
        _config = config;

        Title = Strings.LauncherSettings_Title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 360;

        var root = new StackPanel { Margin = new Thickness(20) };

        var languageRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        languageRow.Children.Add(new TextBlock
        {
            Text = Strings.LauncherSettings_LanguageLabel, Foreground = Fg,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        });
        _languages = new ComboBox
        {
            Background = FieldBg, Foreground = Brushes.Black, MinWidth = 180,
            DisplayMemberPath = nameof(LauncherLanguage.DisplayName), VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var lang in LauncherLanguages.All)
            _languages.Items.Add(lang);
        _languages.SelectedItem = LauncherLanguages.ForCode(config.Language);
        languageRow.Children.Add(_languages);
        root.Children.Add(languageRow);

        _autoReconnect = new CheckBox
        {
            Content = Strings.LauncherSettings_AutoReconnect,
            IsChecked = config.AutoReconnectSingleInstance,
            Foreground = Fg,
            ToolTip = Strings.LauncherSettings_AutoReconnectTip,
            Margin = new Thickness(0, 0, 0, 18),
        };
        root.Children.Add(_autoReconnect);

        // Login autostart: a Startup shortcut that opens the launcher with --start-server at login, which starts
        // the server and manages it. No elevation, so it applies immediately on click, not deferred to Save.
        _loginOpen = new CheckBox
        {
            Content = Strings.LauncherSettings_LoginOpen,
            IsChecked = LoginShortcut.Exists(Environment.ProcessPath ?? ""),
            Foreground = Fg,
            ToolTip = Strings.LauncherSettings_LoginOpenTip,
            Margin = new Thickness(0, 0, 0, 18),
        };
        _loginOpen.Click += OnToggleLoginOpen;
        root.Children.Add(_loginOpen);

        // Server-behavior toggles (persist on Save). Moved here from the main-window Misc box to free room there.
        _hideSteamCmd = new CheckBox
        {
            Content = Strings.Main_HideSteamCmd,
            IsChecked = config.HideSteamCmdWindow,
            Foreground = Fg,
            ToolTip = Strings.Main_HideSteamCmdTip,
            Margin = new Thickness(0, 0, 0, 18),
        };
        root.Children.Add(_hideSteamCmd);

        _logHealthStats = new CheckBox
        {
            Content = Strings.Main_LogServerStatus,
            IsChecked = config.LogHealthStats,
            Foreground = Fg,
            ToolTip = Strings.Main_LogServerStatusTip,
            Margin = new Thickness(0, 0, 0, 18),
        };
        root.Children.Add(_logHealthStats);

        var bottom = new DockPanel { LastChildFill = false };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var licensesLink = new Hyperlink(new Run(Strings.LauncherSettings_ThirdPartyLicenses)) { Foreground = LinkFg };
        licensesLink.Click += (_, _) => ShowLicenses();
        var licensesLine = new TextBlock();
        licensesLine.Inlines.Add(licensesLink);
        info.Children.Add(licensesLine);
        info.Children.Add(new TextBlock
        {
            Text = $"v{AppVersion()} · {Strings.LauncherSettings_Copyright}", Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
        });
        DockPanel.SetDock(info, Dock.Left);
        bottom.Children.Add(info);

        var saveCancel = new StackPanel { Orientation = Orientation.Horizontal };
        saveCancel.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        saveCancel.Children.Add(MakeButton(Strings.Common_Cancel, Close));
        DockPanel.SetDock(saveCancel, Dock.Right);
        bottom.Children.Add(saveCancel);
        root.Children.Add(bottom);

        Content = root;
    }

    /// <summary>Show the dialog modally; returns true if the user saved a change.</summary>
    public static bool Show(Window? owner, LauncherConfig config)
    {
        var dialog = new LauncherSettingsDialog(config) { Owner = owner };
        dialog.ShowDialog();
        return dialog._changed;
    }

    private void OnSave()
    {
        _config.AutoReconnectSingleInstance = _autoReconnect.IsChecked == true;
        _config.HideSteamCmdWindow = _hideSteamCmd.IsChecked == true;
        _config.LogHealthStats = _logHealthStats.IsChecked == true;

        if (_languages.SelectedItem is LauncherLanguage lang &&
            !string.Equals(lang.Code, _config.Language, StringComparison.OrdinalIgnoreCase))
        {
            _config.Language = lang.Code;
            _changed = true; // the caller offers to restart the launcher to apply the new language
        }
        _config.Save();
        Close();
    }
    /// <summary>Toggle the login-open Startup shortcut (no elevation). Applies immediately, reverting the
    /// checkbox if creating or removing the shortcut failed.</summary>
    private void OnToggleLoginOpen(object sender, RoutedEventArgs e)
    {
        var want = _loginOpen.IsChecked == true;
        var exe = Environment.ProcessPath ?? "";
        var ok = want ? LoginShortcut.Create(exe) : LoginShortcut.Remove(exe);
        if (!ok)
        {
            ChoiceDialog.Show(this, Strings.LauncherSettings_LoginOpenFailedTitle,
                Strings.LauncherSettings_LoginOpenFailedMessage, Strings.Common_OK);
            _loginOpen.IsChecked = !want;
        }
    }

    /// <summary>The app version, read from the assembly (baked in from the csproj &lt;Version&gt; at build time,
    /// so it never drifts). Strips the SDK's <c>+&lt;commit&gt;</c> build-metadata suffix, giving e.g. "0.4.0".</summary>
    private static string AppVersion()
    {
        var info = typeof(LauncherSettingsDialog).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return typeof(LauncherSettingsDialog).Assembly.GetName().Version?.ToString(3) ?? "?";
    }

    /// <summary>Show the bundled third-party license notices in a scrollable read-only window.</summary>
    private void ShowLicenses()
    {
        var box = new TextBox
        {
            Text = ReadNotices(), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            Background = Theme.Window, Foreground = Fg,
            BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Padding = new Thickness(12), CaretBrush = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        new Window
        {
            Title = Strings.LauncherSettings_ThirdPartyLicenses, Owner = this,
            Background = Theme.Window,
            Width = 660, Height = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, Content = box,
        }.ShowDialog();
    }

    /// <summary>Read the embedded THIRD-PARTY-NOTICES.md (bundled from the repo root) so the license text has a
    /// single source of truth shared with the repo file.</summary>
    private static string ReadNotices()
    {
        var asm = typeof(LauncherSettingsDialog).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("THIRD-PARTY-NOTICES.md", System.StringComparison.OrdinalIgnoreCase));
        if (name is null)
            return "";
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
            return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

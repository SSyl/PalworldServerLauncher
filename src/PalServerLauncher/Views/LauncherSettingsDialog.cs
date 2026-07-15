using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Launcher-level preferences (currently just the UI language). A dark modal built in code, mirroring the
/// other one-off dialogs. On Save it persists the chosen language. Returns true if the language changed, so
/// the caller can restart the launcher to apply it (restart-to-apply, not a live switch).
/// </summary>
public sealed class LauncherSettingsDialog : Window
{
    private readonly LauncherConfig _config;
    private readonly ComboBox _languages;
    private bool _changed;

    private LauncherSettingsDialog(LauncherConfig config)
    {
        _config = config;

        Title = Strings.LauncherSettings_Title;
        Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x2F, 0x2F));
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

        var bottom = new DockPanel { LastChildFill = false };

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var licensesLink = new Hyperlink(new Run(Strings.LauncherSettings_ThirdPartyLicenses)) { Foreground = LinkFg };
        licensesLink.Click += (_, _) => ShowLicenses();
        var licensesLine = new TextBlock();
        licensesLine.Inlines.Add(licensesLink);
        info.Children.Add(licensesLine);
        info.Children.Add(new TextBlock
        {
            Text = Strings.LauncherSettings_Copyright, Foreground = Muted, FontSize = 11, Margin = new Thickness(0, 3, 0, 0),
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
        if (_languages.SelectedItem is LauncherLanguage lang &&
            !string.Equals(lang.Code, _config.Language, StringComparison.OrdinalIgnoreCase))
        {
            _config.Language = lang.Code;
            _config.Save();
            _changed = true; // the caller offers to restart the launcher to apply the new language
        }
        Close();
    }
    /// <summary>Show the bundled third-party license notices in a scrollable read-only window.</summary>
    private void ShowLicenses()
    {
        var box = new TextBox
        {
            Text = ReadNotices(), IsReadOnly = true, TextWrapping = TextWrapping.NoWrap,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)), Foreground = Fg,
            BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas"), FontSize = 12,
            Padding = new Thickness(12), CaretBrush = Brushes.Transparent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        new Window
        {
            Title = Strings.LauncherSettings_ThirdPartyLicenses, Owner = this,
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)),
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

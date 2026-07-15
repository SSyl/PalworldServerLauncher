using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// First-run language chooser: a dark modal with the localized prompt, a language dropdown (default
/// English), and an OK button. Returns the chosen culture code. Built in code like the other one-off
/// dialogs (mirrors <see cref="NumberPromptDialog"/>). Shown before MainWindow exists, so it has no owner.
/// </summary>
public sealed class LanguagePickerDialog : Window
{
    private readonly ComboBox _languages;
    private string _result;

    private LanguagePickerDialog(string currentCode)
    {
        _result = LauncherLanguages.ForCode(currentCode).Code;

        Title = Strings.Common_AppName;
        Background = new SolidColorBrush(Color.FromRgb(0x2F, 0x2F, 0x2F));
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 360;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = Strings.FirstRun_Prompt, Foreground = Fg, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420, Margin = new Thickness(0, 0, 0, 14),
        });

        _languages = new ComboBox
        {
            Background = FieldBg, Foreground = Brushes.Black, MinWidth = 180,
            DisplayMemberPath = nameof(LauncherLanguage.DisplayName),
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 18),
        };
        foreach (var lang in LauncherLanguages.All)
            _languages.Items.Add(lang);
        _languages.SelectedItem = LauncherLanguages.ForCode(currentCode);
        root.Children.Add(_languages);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeButton(Strings.Common_OK, OnOk));
        root.Children.Add(buttons);

        Content = root;
    }

    /// <summary>Show the picker modally; returns the chosen culture code (defaults to English).</summary>
    public static string Show(string currentCode)
    {
        var dialog = new LanguagePickerDialog(currentCode);
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnOk()
    {
        if (_languages.SelectedItem is LauncherLanguage lang)
            _result = lang.Code;
        Close();
    }
}

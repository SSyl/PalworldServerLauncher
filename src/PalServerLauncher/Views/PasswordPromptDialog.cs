using System.Windows;
using System.Windows.Controls;
using PalServerLauncher.Localization;

namespace PalServerLauncher.Views;

/// <summary>
/// Modal masked-password prompt for the Steam sign-in. Collects the password so the launcher can hand it
/// straight to SteamCMD (<see cref="Core.SteamCmd.ConnectAccountAsync"/>) instead of leaving the user at
/// SteamCMD's own blank, no-echo password prompt. The value is returned to the caller and is never stored
/// or logged.
/// </summary>
public sealed class PasswordPromptDialog : Window
{
    private readonly PasswordBox _passwordBox;
    private bool _accepted;

    private PasswordPromptDialog(string accountName)
    {
        Title = Strings.Mods_PasswordTitle;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 380;

        var root = new StackPanel { Margin = Metrics.DialogPadding };

        root.Children.Add(new TextBlock
        {
            Text = string.Format(Strings.Mods_PasswordPrompt, accountName),
            Foreground = Theme.Text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 0, 0, 10),
        });

        // PasswordBox isn't covered by the app-wide TextBox style, so theme it by hand to match the dark fields.
        _passwordBox = new PasswordBox
        {
            Background = Theme.Field,
            Foreground = Theme.Text,
            BorderBrush = Theme.FieldBorder,
            BorderThickness = new Thickness(1),
            CaretBrush = Theme.Text,
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 13,
        };
        root.Children.Add(_passwordBox);

        root.Children.Add(new TextBlock
        {
            Text = Strings.Mods_PasswordNote,
            Foreground = Theme.TextMuted,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 8, 0, 18),
        });

        var okButton = new Button
        {
            Content = Strings.Common_OK, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90, IsDefault = true, IsEnabled = false,
        };
        okButton.Click += (_, _) => Accept();
        var cancelButton = new Button
        {
            Content = Strings.Common_Cancel, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90, IsCancel = true,
        };
        cancelButton.Click += (_, _) => Close();

        // Gate OK (and thus Enter, since it's the default button) on a non-empty password so an empty submit
        // can't reach SteamCMD.
        _passwordBox.PasswordChanged += (_, _) => okButton.IsEnabled = _passwordBox.Password.Length > 0;

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttonRow.Children.Add(okButton);
        buttonRow.Children.Add(cancelButton);
        root.Children.Add(buttonRow);

        Content = root;
        Loaded += (_, _) => _passwordBox.Focus();
    }

    private void Accept()
    {
        _accepted = true;
        Close();
    }

    /// <summary>Prompt for the Steam password. Returns the entered password, or null if the user cancelled or
    /// closed the dialog. Not retained by the dialog after it closes.</summary>
    public static string? Show(Window? owner, string accountName)
    {
        var dialog = new PasswordPromptDialog(accountName) { Owner = owner };
        dialog.ShowDialog();
        return dialog._accepted ? dialog._passwordBox.Password : null;
    }
}

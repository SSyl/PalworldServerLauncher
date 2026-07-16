using System.Windows;
using System.Windows.Controls;

namespace PalServerLauncher.Views;

/// <summary>
/// Minimal dark-themed modal with a message and 2-3 custom-labeled buttons. Returns the index of
/// the clicked button, or -1 if dismissed. Built in code to avoid another XAML file for a one-off.
/// </summary>
public sealed class ChoiceDialog : Window
{
    private int _result = -1;

    private ChoiceDialog(string title, string message, string[] buttons)
    {
        Title = title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 380;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Theme.Text,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Margin = new Thickness(0, 0, 0, 18),
        });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            var button = new Button
            {
                Content = buttons[i],
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 90,
            };
            button.Click += (_, _) => { _result = index; Close(); };
            buttonRow.Children.Add(button);
        }

        root.Children.Add(buttonRow);
        Content = root;
    }

    /// <summary>Show the dialog modally; returns the clicked button index, or -1 if closed.</summary>
    public static int Show(Window? owner, string title, string message, params string[] buttons)
    {
        var dialog = new ChoiceDialog(title, message, buttons) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }
}

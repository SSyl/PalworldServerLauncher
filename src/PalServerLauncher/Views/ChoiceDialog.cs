using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Minimal dark-themed modal with a message and 2-3 custom-labeled buttons. Returns the index of
/// the clicked button, or -1 if dismissed. Built in code to avoid another XAML file for a one-off.
/// </summary>
public sealed class ChoiceDialog : Window
{
    private int _result = -1;

    private ChoiceDialog(string title, string message, string? detail, string? footer, string[] buttons)
    {
        Title = title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 380;

        // Another program's output can't be reflowed, so it gets a wider dialog than our own prose needs.
        var textWidth = detail is null ? 460 : 600;

        var root = new StackPanel { Margin = Metrics.DialogPadding };

        root.Children.Add(new TextBlock
        {
            Text = message,
            Foreground = Theme.Text,
            FontSize = Metrics.FontBody,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = textWidth,
            Margin = new Thickness(0, 0, 0, Metrics.S16),
        });

        if (detail is not null)
            root.Children.Add(MakeDetailBox(detail, textWidth));

        if (footer is not null)
            root.Children.Add(new TextBlock
            {
                Text = footer,
                Foreground = Theme.Text,
                FontSize = Metrics.FontBody,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = textWidth,
                Margin = new Thickness(0, 0, 0, Metrics.S16),
            });

        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        for (var i = 0; i < buttons.Length; i++)
        {
            var index = i;
            buttonRow.Children.Add(MakeButton(buttons[i], () => { _result = index; Close(); }));
        }

        root.Children.Add(buttonRow);
        Content = root;
    }

    /// <summary>Verbatim output from another program. A read-only TextBox rather than a TextBlock so the text
    /// can be selected and copied, which for an undocumented error code is the point of showing it.</summary>
    private static TextBox MakeDetailBox(string detail, double width) => new()
    {
        Text = detail,
        IsReadOnly = true,
        IsReadOnlyCaretVisible = false,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        // The app-wide TextBox style centers content vertically, which floats a multi-line block off the top.
        VerticalContentAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MaxHeight = 140,
        MaxWidth = width,
        FontFamily = new FontFamily("Consolas"),
        FontSize = Metrics.FontCaption,
        Background = Theme.Sunken,
        Foreground = Theme.TextMuted,
        BorderBrush = Theme.FieldBorder,
        BorderThickness = new Thickness(1),
        Padding = new Thickness(Metrics.S6),
        Margin = new Thickness(0, 0, 0, Metrics.S16),
    };

    /// <summary>Show the dialog modally; returns the clicked button index, or -1 if closed.</summary>
    public static int Show(Window? owner, string title, string message, params string[] buttons) =>
        ShowWithDetail(owner, title, message, null, null, buttons);

    /// <summary>As <see cref="Show"/>, with a selectable block of verbatim output between message and buttons,
    /// and an optional line under it. A question about what to do next belongs below the evidence it follows
    /// from. Separately named because an overload taking a string before a params string[] would silently
    /// swallow the first button label of every existing call.</summary>
    public static int ShowWithDetail(
        Window? owner, string title, string message, string? detail, string? footer, params string[] buttons)
    {
        var dialog = new ChoiceDialog(title, message, detail, footer, buttons) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }
}

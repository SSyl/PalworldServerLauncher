using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Minimal dark-themed modal that asks for a single whole number, pre-filled with a default and gated to
/// digits, with OK / Cancel. Returns the entered value clamped to [min, max], or null if cancelled or
/// dismissed. Built in code like the other one-off dialogs (mirrors <see cref="ChoiceDialog"/>).
/// </summary>
public sealed class NumberPromptDialog : Window
{
    private readonly TextBox _input;
    private readonly int _default;
    private readonly int _min;
    private readonly int _max;
    private int? _result;

    private NumberPromptDialog(string title, string message, string unitLabel, int defaultValue, int min, int max)
    {
        _default = defaultValue;
        _min = min;
        _max = max;

        Title = title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ShowInTaskbar = false;
        MinWidth = 360;

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = message, Foreground = Fg, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420, Margin = new Thickness(0, 0, 0, 14),
        });

        var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        _input = new TextBox
        {
            Text = defaultValue.ToString(CultureInfo.InvariantCulture), Width = 80,
            Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
            Padding = new Thickness(5, 4, 5, 4), CaretBrush = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        DigitsOnly(_input);
        inputRow.Children.Add(_input);
        if (!string.IsNullOrEmpty(unitLabel))
            inputRow.Children.Add(new TextBlock
            {
                Text = unitLabel, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0),
            });
        root.Children.Add(inputRow);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(MakeButton(Strings.Common_OK, OnOk));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));
        root.Children.Add(buttons);

        Content = root;
        Loaded += (_, _) => { _input.Focus(); _input.SelectAll(); };
    }

    /// <summary>Show the prompt modally; returns the value clamped to [min, max], or null if cancelled.</summary>
    public static int? Show(Window? owner, string title, string message, string unitLabel, int defaultValue, int min, int max)
    {
        var dialog = new NumberPromptDialog(title, message, unitLabel, defaultValue, min, max) { Owner = owner };
        dialog.ShowDialog();
        return dialog._result;
    }

    private void OnOk()
    {
        var parsed = int.TryParse(_input.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : _default;
        _result = Math.Clamp(parsed, _min, _max);
        Close();
    }
}

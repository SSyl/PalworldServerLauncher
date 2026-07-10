using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PalServerLauncher.Views;

/// <summary>
/// A masked password or token entry with a Show/Hide reveal toggle, styled to match the dark dialogs. It is
/// masked by default (a <see cref="PasswordBox"/>). The toggle swaps to a plain <see cref="TextBox"/> so the
/// value can be read or copied. The launcher generates a random admin password, so it must be viewable.
/// The reveal toggle always works, even when the field is not editable (the server is running), so you can
/// still check a password without stopping the server. Only editing is gated by <c>editable</c>. Typed and
/// pasted input excludes the double-quote and backslash that Palworld's ini string parser cannot represent.
/// </summary>
internal sealed class SecretField
{
    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    private static readonly Brush ButtonBg = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    private readonly PasswordBox _masked;
    private readonly TextBox _revealed;

    /// <summary>The control to place in a dialog row.</summary>
    public FrameworkElement Element { get; }

    public SecretField(string value, bool editable)
    {
        // Masked view: disabled (grayed, non-editable) when not editable, matching the rest of the dialog.
        _masked = new PasswordBox
        {
            Password = value, IsEnabled = editable, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
            Padding = new Thickness(4, 3, 4, 3), CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Revealed view: read-only (not disabled) when not editable, so the value stays selectable and copyable.
        _revealed = new TextBox
        {
            Text = value, IsReadOnly = !editable, Visibility = Visibility.Collapsed, Background = FieldBg, Foreground = Fg,
            BorderBrush = FieldBorder, Padding = new Thickness(4, 3, 4, 3), CaretBrush = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        GateText(_revealed);
        StripPassword(_masked);

        // Always enabled, even when read-only, so you can reveal a password to view it while the server runs.
        var toggle = new ToggleButton
        {
            Content = "Show", Width = 52, Margin = new Thickness(6, 0, 0, 0), Foreground = Fg,
            Background = ButtonBg, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch, ToolTip = "Show or hide the value",
        };
        toggle.Checked += (_, _) =>
        {
            _revealed.Text = _masked.Password;
            _masked.Visibility = Visibility.Collapsed;
            _revealed.Visibility = Visibility.Visible;
            toggle.Content = "Hide";
        };
        toggle.Unchecked += (_, _) =>
        {
            _masked.Password = _revealed.Text;
            _revealed.Visibility = Visibility.Collapsed;
            _masked.Visibility = Visibility.Visible;
            toggle.Content = "Show";
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_masked, 0);
        Grid.SetColumn(_revealed, 0);
        Grid.SetColumn(toggle, 1);
        grid.Children.Add(_masked);
        grid.Children.Add(_revealed);
        grid.Children.Add(toggle);
        Element = grid;
    }

    /// <summary>The current value, read from whichever view (masked or revealed) is active.</summary>
    public string Value => _revealed.Visibility == Visibility.Visible ? _revealed.Text : _masked.Password;

    /// <summary>Set both views (used for reset-to-default).</summary>
    public void SetValue(string value)
    {
        _masked.Password = value;
        _revealed.Text = value;
    }

    /// <summary>Invoke <paramref name="handler"/> whenever the value changes in either view.</summary>
    public void OnChanged(Action handler)
    {
        _masked.PasswordChanged += (_, _) => handler();
        _revealed.TextChanged += (_, _) => handler();
    }

    private static void GateText(TextBox box)
    {
        box.PreviewTextInput += (_, e) =>
        {
            foreach (var c in e.Text)
                if (c is '"' or '\\') { e.Handled = true; return; }
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string s && s.IndexOfAny(new[] { '"', '\\' }) >= 0)
                e.CancelCommand();
        });
    }

    private static void StripPassword(PasswordBox box)
    {
        var reentrant = false; // setting Password re-fires PasswordChanged - guard against the loop
        box.PasswordChanged += (_, _) =>
        {
            if (reentrant) return;
            var clean = new string(box.Password.Where(c => c is not ('"' or '\\')).ToArray());
            if (clean != box.Password)
            {
                reentrant = true;
                box.Password = clean;
                reentrant = false;
            }
        };
    }
}

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

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
    private readonly PasswordBox _masked;
    private readonly TextBox _revealed;

    /// <summary>The control to place in a dialog row.</summary>
    public FrameworkElement Element { get; }

    public SecretField(string value, bool editable)
    {
        // The masked and revealed views are transparent and borderless. The outer Border (built at the end)
        // draws the field box, so the eye toggle sits INSIDE it like a native Windows password box, not beside it.
        // Masked view: disabled (grayed) when not editable, matching the rest of the dialog.
        _masked = new PasswordBox
        {
            Password = value, IsEnabled = editable, Background = Brushes.Transparent, Foreground = Fg,
            BorderThickness = new Thickness(0), Padding = new Thickness(5, 4, 5, 4), CaretBrush = Brushes.White,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Revealed view: read-only (not disabled) when not editable, so the value stays selectable and copyable.
        _revealed = new TextBox
        {
            Text = value, IsReadOnly = !editable, Visibility = Visibility.Collapsed, Background = Brushes.Transparent,
            Foreground = Fg, BorderThickness = new Thickness(0), Padding = new Thickness(5, 4, 5, 4),
            CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center,
        };
        GateText(_revealed);
        StripPassword(_masked);

        // The eye toggle, inside the field on the right. Transparent + borderless so it blends into the box (it
        // still uses the app-wide Button template for the square hover feedback). Always enabled, even when the
        // field is read-only, so a password can be revealed to view it while the server runs.
        var toggle = new Button
        {
            Content = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13, Foreground = Fg,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), MinWidth = 0,
            Padding = new Thickness(9, 0, 9, 0), Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch, ToolTip = Strings.Secret_Tooltip,
        };
        toggle.Click += (_, _) =>
        {
            if (_masked.Visibility == Visibility.Visible) // currently masked -> reveal
            {
                _revealed.Text = _masked.Password;
                _masked.Visibility = Visibility.Collapsed;
                _revealed.Visibility = Visibility.Visible;
            }
            else // currently revealed -> mask again
            {
                _masked.Password = _revealed.Text;
                _revealed.Visibility = Visibility.Collapsed;
                _masked.Visibility = Visibility.Visible;
            }
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

        // The field box: the border + background that make the input and eye read as one native-style field.
        Element = new Border
        {
            Background = FieldBg, BorderBrush = FieldBorder, BorderThickness = new Thickness(1), Child = grid,
        };
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

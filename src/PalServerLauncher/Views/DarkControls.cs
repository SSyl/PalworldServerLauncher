using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PalServerLauncher.Views;

/// <summary>
/// Shared dark-theme palette and control builders for the code-built dialogs. Each dialog does
/// <c>using static PalServerLauncher.Views.DarkControls;</c> so its existing references (<c>Fg</c>,
/// <c>MakeButton(...)</c>, <c>Header(...)</c>, <c>Field(...)</c>, <c>OpenUrl(...)</c>) resolve here instead of
/// the dialog redeclaring its own copy. This is the single source of truth for the dialog chrome style.
///
/// The genuinely context-dependent builders stay local in each dialog (they are not one style): the two
/// checkbox forms (row-aligned vs stack-spaced), the per-dialog Row layouts, the amber Note/Banner boxes
/// (different margins), and the Blurb/DocBlurb hyperlink helpers (different signatures). Those still pull
/// their colors from the palette here.
/// </summary>
internal static class DarkControls
{
    // ---- palette (the dark theme's core brushes) ----
    public static readonly Brush Fg = Brushes.White;
    public static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    public static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    public static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    public static readonly Brush LinkFg = new SolidColorBrush(Color.FromRgb(0x5A, 0xA0, 0xE0));

    // ---- builders (one canonical style, shared by every dialog) ----

    /// <summary>A section header. Pass a background to a colored button, else it uses the default dark grey.</summary>
    public static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6),
    };

    /// <summary>A dark dialog button. <paramref name="background"/> overrides the default grey for colored buttons.</summary>
    public static Button MakeButton(string label, Action onClick, Brush? background = null)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7),
            Foreground = Fg, Background = background ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A)),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, MinWidth = 90,
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A dark single-line text box. <paramref name="enabled"/> greys it out when false.</summary>
    public static TextBox Field(string value, bool enabled = true) => new()
    {
        Text = value, IsEnabled = enabled, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
        Padding = new Thickness(5, 4, 5, 4), CaretBrush = Brushes.White,
    };

    /// <summary>Open a URL in the default browser, silently ignoring a missing or blocked browser.</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser / launch blocked, nothing useful to do here.
        }
    }

    /// <summary>Gate a text box to ASCII digits only, for both typed and pasted input.</summary>
    public static void DigitsOnly(TextBox box)
    {
        box.PreviewTextInput += (_, e) =>
        {
            foreach (var c in e.Text)
                if (!char.IsAsciiDigit(c)) { e.Handled = true; return; }
        };
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is string s)
                foreach (var c in s)
                    if (!char.IsAsciiDigit(c)) { e.CancelCommand(); return; }
        });
    }
}

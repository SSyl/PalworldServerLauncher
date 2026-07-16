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
    // ---- palette: aliases into the central Theme so each colour is defined once (Theme.cs) ----
    public static readonly Brush Fg = Theme.Text;
    public static readonly Brush Muted = Theme.TextMuted;
    public static readonly Brush FieldBg = Theme.Field;
    public static readonly Brush FieldBorder = Theme.FieldBorder;
    public static readonly Brush LinkFg = Theme.Link;

    // ---- builders (one canonical style, shared by every dialog) ----

    /// <summary>A section header. Pass a background to a colored button, else it uses the default dark grey.</summary>
    public static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6),
    };

    /// <summary>A dark dialog button. Its fill, border, padding, and hover come from the app-wide Button style
    /// (App.xaml), so this only sets layout. <paramref name="background"/> overrides the grey fill for colored
    /// (e.g. danger) buttons.</summary>
    public static Button MakeButton(string label, Action onClick, Brush? background = null)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), MinWidth = 90,
        };
        if (background is not null)
            button.Background = background;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A dark single-line text box. Colours, padding, and the Accent hover come from the app-wide
    /// TextBox style (App.xaml), so this only sets content. <paramref name="enabled"/> greys it out when false.</summary>
    public static TextBox Field(string value, bool enabled = true) => new()
    {
        Text = value, IsEnabled = enabled,
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

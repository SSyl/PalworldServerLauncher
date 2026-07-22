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

    // ---- promoted builders (these were copy-pasted across the dialogs at divergent sizes) ----

    /// <summary>The app's caret colour, so dark inputs stop leaking a hard <c>Brushes.White</c>.</summary>
    public static readonly Brush Caret = Theme.Caret;

    /// <summary>An amber note / callout box wrapping arbitrary content (e.g. a hyperlink blurb). The several
    /// hand-rolled copies across the dialogs collapse to this. Default top margin is one spacing step.</summary>
    public static Border NoteBox(UIElement content, Thickness? margin = null) => new()
    {
        Background = Theme.BannerBg,
        Padding = new Thickness(10, 8, 10, 8),
        Margin = margin ?? new Thickness(0, Metrics.S8, 0, 0),
        Child = content,
    };

    /// <summary>An amber note box around a plain wrapping string.</summary>
    public static Border Note(string text, Thickness? margin = null) =>
        NoteBox(new TextBlock { Text = text, Foreground = Theme.BannerFg, TextWrapping = TextWrapping.Wrap }, margin);

    /// <summary>A themed checkbox. Near-identical copies lived in DiscordDialog, ModsDialog and LauncherSettings.</summary>
    public static CheckBox Check(string text, bool value, Thickness? margin = null) => new()
    {
        Content = text, IsChecked = value, Foreground = Fg,
        VerticalContentAlignment = VerticalAlignment.Center,
        Margin = margin ?? new Thickness(0),
    };

    /// <summary>A label + input row with a fixed-width label column, the grid several dialogs hand-rolled at
    /// divergent label widths (Discord 200, Port Check 190). Pass <paramref name="labelWidth"/> to match.</summary>
    public static Grid Row(string label, FrameworkElement input, double labelWidth = 200)
    {
        var grid = new Grid { Margin = new Thickness(0, Metrics.S4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);
        return grid;
    }

    /// <summary>A square Segoe MDL2 glyph button (send, history, and similar). Lifted from ServerCommandsDialog.</summary>
    public static Button IconButton(string glyph, Action onClick, string? tooltip = null)
    {
        var button = new Button
        {
            Content = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = Metrics.FontIcon,
            MinWidth = Metrics.IconButtonSize,
            Margin = new Thickness(Metrics.S8, 0, 0, 0),
        };
        if (tooltip is not null) button.ToolTip = tooltip;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A small "reset to default" glyph button (MDL2 undo), shared by the settings editor's per-field
    /// resets and the backup-location reset. The caller wires any show / hide logic.</summary>
    public static Button ResetButton(Action onClick, string? tooltip = null)
    {
        var button = new Button
        {
            Content = "",  // MDL2 Undo
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Background = Theme.Control,
            BorderThickness = new Thickness(0),
            MinWidth = 26,
            Padding = new Thickness(0, 1, 0, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        if (tooltip is not null) button.ToolTip = tooltip;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A close / remove glyph button (a gray box that turns red on hover) sharing the app's
    /// CloseButtonStyle with the scheduler's XAML delete buttons. Pass <paramref name="squareSize"/> for a
    /// square variant (table cells like the mods panel); the default is a wider rectangle for list rows.</summary>
    public static Button CloseButton(Action onClick, string? tooltip = null, double squareSize = 0)
    {
        var button = new Button
        {
            Content = "", // MDL2 ChromeClose
            Style = (Style)Application.Current.FindResource("CloseButtonStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // A square variant (equal size, centered glyph) for table cells like the mods panel, where the default
        // wide-rectangle shape reads oddly. The rectangle stays the default for list rows (the scheduler).
        if (squareSize > 0)
        {
            button.Width = button.Height = squareSize;
            button.Padding = new Thickness(0);
        }
        if (tooltip is not null) button.ToolTip = tooltip;
        button.Click += (_, _) => onClick();
        return button;
    }
}

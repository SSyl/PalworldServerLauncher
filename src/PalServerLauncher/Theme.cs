using System.Windows.Media;

namespace PalServerLauncher;

/// <summary>
/// The single source of truth for the app's dark theme. Both the XAML styles (App.xaml and MainWindow.xaml,
/// via <c>{x:Static local:Theme.X}</c>) and the code-built dialogs (<see cref="Views.DarkControls"/> and the
/// per-dialog builders) reference these brushes, so each colour is defined once here. Change a value here and
/// it re-themes the whole app instead of hunting down one-off hex literals. Brushes are frozen for perf and
/// cross-thread safety.
/// </summary>
public static class Theme
{
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // Surfaces
    public static readonly SolidColorBrush Window = Frozen(0x1E, 0x1E, 0x1E);       // dialog / window background (all dialogs)
    public static readonly SolidColorBrush Sunken = Frozen(0x14, 0x14, 0x14);       // recessed: tab strip, selected tab, code preview
    public static readonly SolidColorBrush Inset = Frozen(0x26, 0x26, 0x26);        // raised note / inset panels

    // Buttons (Windows 11 style: subtle grey fill plus a faint border)
    public static readonly SolidColorBrush Button = Frozen(0x2D, 0x2D, 0x2D);
    public static readonly SolidColorBrush ButtonBorder = Frozen(0x3D, 0x3D, 0x3D);
    public static readonly SolidColorBrush Control = Frozen(0x44, 0x44, 0x44);      // small inline icon buttons (reveal, reset, remove), lighter than fields

    // Accent (selection, tab underline, focus)
    public static readonly SolidColorBrush Accent = Frozen(0x2D, 0x6C, 0xDF);

    // Text
    public static readonly SolidColorBrush Text = Frozen(0xF0, 0xF0, 0xF0);        // primary
    public static readonly SolidColorBrush TextMuted = Frozen(0x99, 0x99, 0x99);   // secondary, unselected tabs
    public static readonly SolidColorBrush TextCaption = Frozen(0x88, 0x88, 0x88); // dim captions

    // Inputs
    public static readonly SolidColorBrush Field = Frozen(0x33, 0x33, 0x33);
    public static readonly SolidColorBrush FieldBorder = Frozen(0x4A, 0x4A, 0x4A);

    // Lines and links
    public static readonly SolidColorBrush Divider = Frozen(0x3A, 0x3A, 0x3A);  // tab separators, hairlines
    public static readonly SolidColorBrush Link = Frozen(0x5A, 0xA0, 0xE0);
    public static readonly SolidColorBrush Error = Frozen(0xE0, 0x5A, 0x5A);    // invalid-input border
    public static readonly SolidColorBrush CodeFg = Frozen(0x9C, 0xD0, 0x9C);   // monospace command preview text

    // Amber warning banner
    public static readonly SolidColorBrush BannerBg = Frozen(0x3A, 0x2E, 0x1E);
    public static readonly SolidColorBrush BannerFg = Frozen(0xE0, 0xC0, 0x80);

    // Status accents (Fluent-style). Danger is the destructive-button red, Error the lighter validation /
    // error-text red, Success/Warning the status greens and ambers.
    public static readonly SolidColorBrush Danger = Frozen(0xC4, 0x2B, 0x1C);      // Fluent critical red button
    public static readonly SolidColorBrush DangerHover = Frozen(0xB1, 0x27, 0x1B);
    public static readonly SolidColorBrush Success = Frozen(0x6C, 0xCB, 0x5F);     // Fluent success green
    public static readonly SolidColorBrush Warning = Frozen(0xE0, 0xB8, 0x4C);     // amber

    // Named action / brand colours (single-role, but kept here so every colour lives in one file)
    public static readonly SolidColorBrush Start = Frozen(0x2E, 0x9E, 0x4A);       // primary Install / Start button
    public static readonly SolidColorBrush Countdown = Frozen(0xD9, 0x9A, 0x2B);   // primary button during a timed-shutdown countdown
    public static readonly SolidColorBrush Discord = Frozen(0x58, 0x65, 0xF2);     // Discord brand
    public static readonly SolidColorBrush DiscordHover = Frozen(0x47, 0x52, 0xC4);
    public static readonly SolidColorBrush LogText = Frozen(0xC8, 0xC8, 0xC8);     // monospace log text
}

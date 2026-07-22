using System.Windows;

namespace PalServerLauncher;

/// <summary>
/// The single source of truth for the app's SIZING system: the spacing scale, type ramp, control dimensions,
/// corner radius, and icon sizing. It is the sibling of <see cref="Theme"/>, which owns COLOR. Both the XAML
/// styles (App.xaml / MainWindow.xaml via <c>{x:Static local:Metrics.X}</c>) and the code-built dialogs
/// reference these, so every dimension is defined once here instead of scattered as literals.
///
/// The look is deliberately DENSE / information-forward (this is a server utility, everything one glance
/// away), so the spacing scale is tight: in-panel gaps live at <see cref="S4"/>..<see cref="S8"/> and
/// <see cref="S16"/> is the window / dialog edge. Change a value here and it re-sizes the whole app.
/// </summary>
public static class Metrics
{
    // Spacing scale (dense, 2px-based). S24 is reserved for rare large separations.
    public const double S2 = 2;
    public const double S4 = 4;
    public const double S6 = 6;
    public const double S8 = 8;
    public const double S12 = 12;
    public const double S16 = 16;
    public const double S24 = 24;

    // Type ramp: four roles. Body is the window base size.
    public const double FontCaption = 11;   // dim labels, suffixes, captions
    public const double FontBody = 13;       // default body text
    public const double FontSubhead = 15;    // section / box headers, the status value
    public const double FontAction = 16;     // the primary action button
    public const double FontIcon = 15;       // Segoe MDL2 glyph buttons

    // Controls: one height shared by buttons / text boxes / combos, and two numeric-field widths keyed to
    // digit count (replacing the old 28/30/38/40 spread) so triplet rows stay dense, plus a square glyph size.
    public const double ControlHeight = 28;
    public const double NumericFieldNarrow = 36;   // 1-3 digit fields (hours, minutes, thresholds)
    public const double NumericFieldWidth = 44;    // 4-digit fields (e.g. update interval in minutes)
    public const double IconButtonSize = 28;

    // Corner radius: a single answer for every container (buttons, fields, combos, boxes). The toggle switch
    // keeps its own pill radius (10) in App.xaml.
    public static readonly CornerRadius Radius = new(4);

    // Common paddings, so a field and a button read at the same rhythm everywhere.
    public static readonly Thickness FieldPadding = new(6, 4, 6, 4);
    public static readonly Thickness ButtonPadding = new(12, 6, 12, 6);
    public static readonly Thickness DialogPadding = new(S16);
}

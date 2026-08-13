using System.Globalization;
using System.Windows.Data;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Views;

/// <summary>
/// Renders one log row from the entry plus the live "show date" setting. A MultiBinding rather than a property
/// on the entry, because flipping the setting has to re-render the rows already on screen, and because the row
/// must stay a single stretched TextBlock for wrapping to work (see the LogListStyle comment in MainWindow.xaml).
/// Pass ConverterParameter="tag" for the General tab.
/// </summary>
public sealed class LogLineConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values is [LogEntry entry, bool showDate]
            ? entry.Render(showDate, withTag: (parameter as string) == "tag")
            : string.Empty;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

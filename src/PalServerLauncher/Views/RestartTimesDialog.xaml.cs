using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PalServerLauncher.Views;

/// <summary>
/// Add/remove restart times with dropdowns (no typing). The hour dropdown and AM/PM follow the OS
/// time format - AM/PM in 12-hour cultures, 00-23 in 24-hour cultures. Returns the chosen times.
/// </summary>
public partial class RestartTimesDialog : Window
{
    public sealed class TimeEntry
    {
        public TimeOnly Time { get; }
        public string Display => Time.ToString("t", CultureInfo.CurrentCulture);
        public TimeEntry(TimeOnly time) => Time = time;
    }

    private readonly ObservableCollection<TimeEntry> _times = new();
    private readonly bool _use12Hour;

    public List<TimeOnly>? Result { get; private set; }

    public RestartTimesDialog(IEnumerable<TimeOnly> initial, string label)
    {
        InitializeComponent();

        Title = $"{label} Times";
        HeaderText.Text = $"{label} times";

        _use12Hour = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern
            .Contains("t", StringComparison.OrdinalIgnoreCase);

        if (_use12Hour)
        {
            HourBox.ItemsSource = new[] { "12", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10", "11" };
            HourBox.SelectedItem = "6";
            AmPmBox.ItemsSource = new[] { "AM", "PM" };
            AmPmBox.SelectedIndex = 0;
        }
        else
        {
            HourBox.ItemsSource = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToArray();
            HourBox.SelectedItem = "06";
            AmPmBox.Visibility = Visibility.Collapsed;
        }

        MinuteBox.ItemsSource = Enumerable.Range(0, 12).Select(m => (m * 5).ToString("00")).ToArray();
        MinuteBox.SelectedItem = "00";

        EveryBox.ItemsSource = new[] { "15m", "30m", "45m", "1h", "2h", "3h", "4h", "6h", "8h", "12h", "24h" };

        TimesList.ItemsSource = _times;
        foreach (var time in initial.Distinct().OrderBy(t => t))
            _times.Add(new TimeEntry(time));
    }

    private void AddTime(object sender, RoutedEventArgs e)
    {
        if (!TryCompose(out var time))
        {
            ChoiceDialog.Show(this, "Invalid time", "Enter minutes as a number 0-59.", "OK");
            return;
        }
        if (_times.Any(x => x.Time == time))
            return;

        var index = 0;
        while (index < _times.Count && _times[index].Time < time)
            index++;
        _times.Insert(index, new TimeEntry(time));
    }

    private void MinuteDigitsOnly(object sender, TextCompositionEventArgs e)
    {
        foreach (var c in e.Text)
            if (!char.IsAsciiDigit(c)) { e.Handled = true; return; }
    }

    private void RemoveTime(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TimeEntry entry })
            _times.Remove(entry);
    }

    private void ApplyEvery(object sender, RoutedEventArgs e)
    {
        var hours = ParseInterval(EveryBox.Text);
        var step = hours is null ? 0 : (int)Math.Round(hours.Value * 60);
        if (step < 15 || step > 24 * 60)
        {
            ChoiceDialog.Show(this, "Invalid interval",
                "Enter an interval between 15 minutes and 24 hours - e.g. 30m, 2h, or 2.5 (hours).", "OK");
            return;
        }

        if (_times.Count > 0)
        {
            var choice = ChoiceDialog.Show(this, "Replace times",
                $"This replaces the {_times.Count} time(s) below with a schedule every {EveryBox.Text.Trim()}. Continue?",
                "Replace", "Cancel");
            if (choice != 0)
                return;
        }

        _times.Clear();
        foreach (var time in EverySchedule(hours!.Value))
            _times.Add(new TimeEntry(time));
    }

    /// <summary>Times every <paramref name="intervalHours"/> from midnight, stopping before 24h (a
    /// non-dividing interval just leaves a shorter final gap). Pure - unit-tested.</summary>
    public static IReadOnlyList<TimeOnly> EverySchedule(double intervalHours)
    {
        var step = (int)Math.Round(intervalHours * 60);
        if (step <= 0)
            return Array.Empty<TimeOnly>();

        var times = new List<TimeOnly>();
        for (var minutes = 0; minutes < 24 * 60; minutes += step)
            times.Add(new TimeOnly(minutes / 60, minutes % 60));
        return times;
    }

    /// <summary>Parse an interval as hours from "15m" / "2h" / a bare decimal like "2.5". Null if unparseable.</summary>
    public static double? ParseInterval(string text)
    {
        text = (text ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0)
            return null;
        if (text.EndsWith('m'))
            return TryNum(text[..^1], out var mins) ? mins / 60.0 : null;
        if (text.EndsWith('h'))
            return TryNum(text[..^1], out var hrs) ? hrs : null;
        return TryNum(text, out var h) ? h : null;
    }

    private static bool TryNum(string s, out double value) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
        || double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    private void Done(object sender, RoutedEventArgs e)
    {
        Result = _times.Select(x => x.Time).ToList();
        DialogResult = true;
        Close();
    }

    private bool TryCompose(out TimeOnly time)
    {
        time = default;
        // MinuteBox is editable: read the typed/selected text and require 0-59.
        if (!int.TryParse((MinuteBox.Text ?? "").Trim(), out var minute) || minute is < 0 or > 59)
            return false;

        int hour;
        if (_use12Hour)
        {
            var hour12 = int.Parse((string)HourBox.SelectedItem);
            var isPm = (string)AmPmBox.SelectedItem == "PM";
            hour = hour12 == 12 ? (isPm ? 12 : 0) : (isPm ? hour12 + 12 : hour12);
        }
        else
        {
            hour = int.Parse((string)HourBox.SelectedItem);
        }
        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>Show the dialog; returns the chosen times, or null if cancelled.</summary>
    public static List<TimeOnly>? Show(Window owner, IEnumerable<TimeOnly> initial, string label = "Restart")
    {
        var dialog = new RestartTimesDialog(initial, label) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}

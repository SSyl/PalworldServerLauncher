using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Edits the in-game announcements: the two restart templates (scheduled/manual vs update) and the
/// optional message repeated on an interval while the server runs. In the restart templates the token
/// <c>{minutes}</c> is replaced at announce time with the minutes remaining, and a template without it is
/// a fixed message. Save requires each message be non-empty (Palworld's <c>/announce</c> rejects a blank
/// body) and warns, with a save-anyway option, if a restart template drops the <c>{minutes}</c> token.
/// </summary>
public sealed class AnnouncementsDialog : Window
{
    private const string Token = "{minutes}";

    private readonly LauncherConfig _config;
    private readonly TextBox _restart, _update, _repeat, _interval;
    private readonly CheckBox _repeatEnabled;
    private bool _saved;

    private AnnouncementsDialog(LauncherConfig config)
    {
        _config = config;

        Title = Strings.Announcements_Title;
        Background = Theme.Window;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = Metrics.DialogPadding };
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Announcements_Intro,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        });

        stack.Children.Add(Label(Strings.Announcements_RestartLabel));
        _restart = Field(_config.RestartAnnounceMessage);
        stack.Children.Add(_restart);

        stack.Children.Add(Label(Strings.Announcements_UpdateLabel));
        _update = Field(_config.UpdateAnnounceMessage);
        stack.Children.Add(_update);

        stack.Children.Add(new Border { Height = 1, Background = Theme.Divider, Margin = new Thickness(0, 18, 0, 14) });

        _repeatEnabled = Check(Strings.Announcements_RepeatEnable, _config.RecurringAnnounceEnabled);
        stack.Children.Add(_repeatEnabled);
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Announcements_RepeatIntro,
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0),
        });

        stack.Children.Add(Label(Strings.Announcements_RepeatLabel));
        _repeat = Field(_config.RecurringAnnounceMessage);
        stack.Children.Add(_repeat);

        _interval = Field(_config.RecurringAnnounceIntervalMinutes.ToString(CultureInfo.InvariantCulture));
        _interval.Width = 66;
        _interval.HorizontalAlignment = HorizontalAlignment.Left;
        DigitsOnly(_interval);
        stack.Children.Add(Row(Strings.Announcements_RepeatIntervalLabel, _interval));

        _repeatEnabled.Checked += (_, _) => GateRepeatFields();
        _repeatEnabled.Unchecked += (_, _) => GateRepeatFields();
        GateRepeatFields();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));
        stack.Children.Add(buttons);

        Content = stack;
    }

    public static bool Show(Window? owner, LauncherConfig config)
    {
        var dialog = new AnnouncementsDialog(config) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private void OnSave()
    {
        var restart = _restart.Text.Trim();
        var update = _update.Text.Trim();
        var repeat = _repeat.Text.Trim();
        var repeatOn = _repeatEnabled.IsChecked == true;

        // Hard rule: the server's /announce rejects an empty body.
        var empty = new List<string>();
        if (restart.Length == 0) empty.Add(Strings.Announcements_RestartLabel);
        if (update.Length == 0) empty.Add(Strings.Announcements_UpdateLabel);
        if (repeatOn && repeat.Length == 0) empty.Add(Strings.Announcements_RepeatLabel);
        if (empty.Count > 0)
        {
            ChoiceDialog.Show(this, Strings.Announcements_EmptyTitle,
                Strings.Announcements_EmptyMessage + "\n" + string.Join("\n", empty), Strings.Common_OK);
            return;
        }

        // Soft warning: a missing token is allowed (fixed message) but the user probably didn't mean to.
        var missing = new List<string>();
        if (!restart.Contains(Token)) missing.Add(Strings.Announcements_RestartLabel);
        if (!update.Contains(Token)) missing.Add(Strings.Announcements_UpdateLabel);
        if (missing.Count > 0)
        {
            var choice = ChoiceDialog.Show(this, Strings.Announcements_NoTokenTitle,
                string.Format(Strings.Announcements_NoTokenMessage, Token, string.Join("\n", missing)),
                Strings.Announcements_SaveAnyway, Strings.Announcements_KeepEditing);
            if (choice != 0)
                return;
        }

        _config.RestartAnnounceMessage = restart;
        _config.UpdateAnnounceMessage = update;
        _config.RecurringAnnounceEnabled = repeatOn;
        _config.RecurringAnnounceMessage = repeat;
        _config.RecurringAnnounceIntervalMinutes = ParsedInterval();
        _config.Save();
        _saved = true;
        Close();
    }

    /// <summary>The typed interval, clamped to the bounds the scheduler enforces anyway (an empty or 0 box
    /// becomes the minimum rather than a silent no-repeat).</summary>
    private int ParsedInterval()
    {
        var minutes = int.TryParse(_interval.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : _config.RecurringAnnounceIntervalMinutes;
        return Math.Clamp(minutes, RecurringAnnouncer.MinIntervalMinutes, RecurringAnnouncer.MaxIntervalMinutes);
    }

    private void GateRepeatFields()
    {
        var on = _repeatEnabled.IsChecked == true;
        _repeat.IsEnabled = on;
        _interval.IsEnabled = on;
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Fg, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4),
    };
}

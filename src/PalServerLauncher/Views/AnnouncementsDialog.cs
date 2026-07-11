using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Config;

namespace PalServerLauncher.Views;

/// <summary>
/// Edits the two in-game restart announcement templates (scheduled/manual vs update). The token
/// <c>{minutes}</c> is replaced at announce time with the minutes remaining; a template without it is a
/// fixed message. Save requires each message be non-empty (Palworld's <c>/announce</c> rejects a blank
/// body) and warns, with a save-anyway option, if a message drops the <c>{minutes}</c> token.
/// </summary>
public sealed class AnnouncementsDialog : Window
{
    private const string Token = "{minutes}";

    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    private readonly LauncherConfig _config;
    private readonly TextBox _restart, _update;
    private bool _saved;

    private AnnouncementsDialog(LauncherConfig config)
    {
        _config = config;

        Title = "Announcements";
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock
        {
            Text = "Shown to players before a restart. Put " + Token + " where the countdown should go " +
                   "(it becomes the minutes remaining). Remove it for a fixed message with no time.",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
        });

        stack.Children.Add(Label("Scheduled / manual restart"));
        _restart = Field(_config.RestartAnnounceMessage);
        stack.Children.Add(_restart);

        stack.Children.Add(Label("Update restart"));
        _update = Field(_config.UpdateAnnounceMessage);
        stack.Children.Add(_update);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(MakeButton("Save", OnSave));
        buttons.Children.Add(MakeButton("Cancel", Close));
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

        // Hard rule: the server's /announce rejects an empty body.
        var empty = new List<string>();
        if (restart.Length == 0) empty.Add("Scheduled / manual restart");
        if (update.Length == 0) empty.Add("Update restart");
        if (empty.Count > 0)
        {
            ChoiceDialog.Show(this, "Empty announcement",
                "An announcement can't be empty (the server rejects a blank message):\n" + string.Join("\n", empty), "OK");
            return;
        }

        // Soft warning: a missing token is allowed (fixed message) but the user probably didn't mean to.
        var missing = new List<string>();
        if (!restart.Contains(Token)) missing.Add("Scheduled / manual restart");
        if (!update.Contains(Token)) missing.Add("Update restart");
        if (missing.Count > 0)
        {
            var choice = ChoiceDialog.Show(this, "No countdown token",
                $"These have no {Token} token, so they won't show the minutes remaining:\n{string.Join("\n", missing)}\n\nSave them as fixed messages anyway?",
                "Save anyway", "Keep editing");
            if (choice != 0)
                return;
        }

        _config.RestartAnnounceMessage = restart;
        _config.UpdateAnnounceMessage = update;
        _config.Save();
        _saved = true;
        Close();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Fg, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4),
    };

    // Single-line by default (AcceptsReturn is false), so announcements stay one line as they render in-game.
    private static TextBox Field(string value) => new()
    {
        Text = value, Background = FieldBg, Foreground = Fg,
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        Padding = new Thickness(6, 5, 6, 5), CaretBrush = Brushes.White,
    };

    private static Button MakeButton(string label, System.Action onClick)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7),
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, MinWidth = 90,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}

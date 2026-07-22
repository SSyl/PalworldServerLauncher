using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

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

    private readonly LauncherConfig _config;
    private readonly TextBox _restart, _update;
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

        // Hard rule: the server's /announce rejects an empty body.
        var empty = new List<string>();
        if (restart.Length == 0) empty.Add(Strings.Announcements_RestartLabel);
        if (update.Length == 0) empty.Add(Strings.Announcements_UpdateLabel);
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
        _config.Save();
        _saved = true;
        Close();
    }

    private static TextBlock Label(string text) => new()
    {
        Text = text, Foreground = Fg, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4),
    };
}

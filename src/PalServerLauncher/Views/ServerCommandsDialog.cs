using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Core;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest.Models;

namespace PalServerLauncher.Views;

/// <summary>
/// Live server commands over the REST API: announce, kick / ban an online player (with a reason), unban by
/// user id, save, shutdown with an in-game countdown, and force stop. Opened only while the server is up.
/// Destructive actions (kick / ban / shutdown / force stop) confirm through <see cref="ChoiceDialog"/> first.
/// Built in code with the async handler pattern from <see cref="PortCheckDialog"/> (handlers are
/// <c>async void</c>, guarded so a REST hiccup can't crash the app).
/// </summary>
public sealed class ServerCommandsDialog : Window
{
    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xC2, 0x42, 0x38));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x0B));

    private readonly ServerCommandActions _actions;
    private readonly Logger _logger;

    private readonly TextBox _announce;
    private readonly TextBox _reason;
    private readonly TextBox _unbanUserId;
    private readonly TextBox _shutdownSeconds;
    private readonly StackPanel _playersPanel;
    private readonly TextBlock _status;

    private ServerCommandsDialog(ServerCommandActions actions, Logger logger)
    {
        _actions = actions;
        _logger = logger;

        Title = "Server Commands";
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(Header("Announce"));
        _announce = Field("");
        _announce.AcceptsReturn = false;
        var announceRow = new Grid();
        announceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        announceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_announce, 0);
        var sendButton = MakeButton("Send", OnSendAnnounce);
        sendButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(sendButton, 1);
        announceRow.Children.Add(_announce);
        announceRow.Children.Add(sendButton);
        stack.Children.Add(announceRow);

        var playersHeader = new Grid { Margin = new Thickness(0, 16, 0, 4) };
        playersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        playersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var playersTitle = Header("Players");
        playersTitle.Margin = new Thickness(0);
        Grid.SetColumn(playersTitle, 0);
        var refreshButton = MakeButton("Refresh", OnRefreshPlayers);
        Grid.SetColumn(refreshButton, 1);
        playersHeader.Children.Add(playersTitle);
        playersHeader.Children.Add(refreshButton);
        stack.Children.Add(playersHeader);

        _reason = Field("");
        stack.Children.Add(Labeled("Reason (kick / ban)", _reason));

        _playersPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _playersPanel.Children.Add(Line("Loading players...", Muted));
        stack.Children.Add(_playersPanel);

        stack.Children.Add(Header("Unban"));
        _unbanUserId = Field("");
        var unbanRow = new Grid();
        unbanRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        unbanRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_unbanUserId, 0);
        var unbanButton = MakeButton("Unban", OnUnban);
        unbanButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(unbanButton, 1);
        unbanRow.Children.Add(_unbanUserId);
        unbanRow.Children.Add(unbanButton);
        stack.Children.Add(unbanRow);
        stack.Children.Add(Line("Banned players aren't online, so unban takes the platform user id (e.g. steam_0123...).", Muted));

        stack.Children.Add(Header("Server"));
        var serverRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        serverRow.Children.Add(MakeButton("Save world", OnSave));
        serverRow.Children.Add(new TextBlock { Text = "Shutdown in", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
        _shutdownSeconds = Field("30");
        _shutdownSeconds.Width = 48;
        DigitsOnly(_shutdownSeconds);
        serverRow.Children.Add(_shutdownSeconds);
        serverRow.Children.Add(new TextBlock { Text = "s", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 6, 0) });
        serverRow.Children.Add(MakeButton("Shutdown", OnShutdown, Warn));
        serverRow.Children.Add(MakeButton("Force Stop", OnForceStop, Danger));
        stack.Children.Add(serverRow);

        _status = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
        stack.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(MakeButton("Close", Close));
        stack.Children.Add(buttons);

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Loaded += (_, _) => OnRefreshPlayers();
    }

    public static void Show(Window? owner, ServerCommandActions actions, Logger logger)
    {
        var dialog = new ServerCommandsDialog(actions, logger) { Owner = owner };
        dialog.ShowDialog();
    }

    private async void OnSendAnnounce() => await Guard(async () =>
    {
        var message = _announce.Text.Trim();
        if (message.Length == 0) { SetStatus("Enter a message to announce."); return; }
        SetStatus(await _actions.Announce(message) ? "Announced." : "Couldn't announce (REST off or rejected).");
    });

    private async void OnRefreshPlayers() => await Guard(async () =>
    {
        _playersPanel.Children.Clear();
        _playersPanel.Children.Add(Line("Loading players...", Muted));

        var players = await _actions.GetPlayers();
        _playersPanel.Children.Clear();
        if (players is null) { _playersPanel.Children.Add(Line("REST API is off or not responding.", Muted)); return; }
        if (players.Players.Count == 0) { _playersPanel.Children.Add(Line("No players online.", Muted)); return; }

        foreach (var player in players.Players)
            _playersPanel.Children.Add(PlayerRow(player));
    });

    private async void OnUnban() => await Guard(async () =>
    {
        var userId = _unbanUserId.Text.Trim();
        if (userId.Length == 0) { SetStatus("Enter a user id to unban."); return; }
        if (ChoiceDialog.Show(this, "Unban player", $"Unban {userId}?", "Unban", "Cancel") != 0) return;
        SetStatus(await _actions.Unban(userId) ? $"Unbanned {userId}." : "Couldn't unban (REST off or rejected).");
    });

    private async void OnSave() => await Guard(async () =>
        SetStatus(await _actions.Save() ? "World saved." : "Couldn't save (REST off or rejected)."));

    private void OnShutdown()
    {
        if (!int.TryParse(_shutdownSeconds.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) || seconds < 1)
        {
            SetStatus("Enter a countdown of 1 second or more.");
            return;
        }
        if (ChoiceDialog.Show(this, "Shut down the server",
                $"Shut the server down in {seconds} seconds? Players see an in-game countdown.", "Shutdown", "Cancel") != 0)
            return;
        FireAndClose(_actions.ShutdownWithCountdown(seconds), "Shutdown");
    }

    private void OnForceStop()
    {
        if (ChoiceDialog.Show(this, "Force stop",
                "Force-stop the server now? This kills it with no countdown (the last autosave limits loss).", "Force Stop", "Cancel") != 0)
            return;
        FireAndClose(_actions.ForceStop(), "Force stop");
    }

    private Grid PlayerRow(Player player)
    {
        var name = player.Name ?? player.AccountName ?? "(unknown)";
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = $"{name}  (Lv {player.Level})", Foreground = Fg, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var hasUserId = !string.IsNullOrEmpty(player.UserId);
        var kick = MakeButton("Kick", () => OnKick(player), Warn);
        var ban = MakeButton("Ban", () => OnBan(player), Danger);
        kick.IsEnabled = ban.IsEnabled = hasUserId;
        if (!hasUserId)
            kick.ToolTip = ban.ToolTip = "No user id reported for this player.";
        Grid.SetColumn(kick, 1);
        Grid.SetColumn(ban, 2);
        grid.Children.Add(kick);
        grid.Children.Add(ban);
        return grid;
    }

    private async void OnKick(Player player) => await Guard(async () =>
    {
        var name = player.Name ?? player.AccountName ?? "(unknown)";
        if (string.IsNullOrEmpty(player.UserId)) return;
        if (ChoiceDialog.Show(this, "Kick player", $"Kick {name}?", "Kick", "Cancel") != 0) return;
        if (await _actions.Kick(player.UserId, _reason.Text.Trim()))
        {
            SetStatus($"Kicked {name}.");
            OnRefreshPlayers();
        }
        else SetStatus($"Couldn't kick {name} (REST off or rejected).");
    });

    private async void OnBan(Player player) => await Guard(async () =>
    {
        var name = player.Name ?? player.AccountName ?? "(unknown)";
        if (string.IsNullOrEmpty(player.UserId)) return;
        if (ChoiceDialog.Show(this, "Ban player", $"Ban {name}? They'll be kicked and blocked from rejoining.", "Ban", "Cancel") != 0) return;
        if (await _actions.Ban(player.UserId, _reason.Text.Trim()))
        {
            SetStatus($"Banned {name}.");
            OnRefreshPlayers();
        }
        else SetStatus($"Couldn't ban {name} (REST off or rejected).");
    });

    private void FireAndClose(Task task, string what)
    {
        _ = LogFailure(task, what);
        Close();
    }

    private async Task LogFailure(Task task, string what)
    {
        try { await task; }
        catch (Exception ex) { _logger.Error($"{what} failed", ex); }
    }

    private async Task Guard(Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            _logger.Error("Server command failed", ex);
            SetStatus("Command failed, see the log.");
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    // --- small dark-theme builders (mirrors PortCheckDialog / DiscordDialog) ---
    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 6),
    };

    private static TextBlock Line(string text, Brush colour) => new()
    {
        Text = text, Foreground = colour, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0),
    };

    private static StackPanel Labeled(string label, FrameworkElement input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        panel.Children.Add(new TextBlock { Text = label, Foreground = Muted, Margin = new Thickness(0, 0, 0, 3) });
        panel.Children.Add(input);
        return panel;
    }

    private static TextBox Field(string value) => new()
    {
        Text = value, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
        Padding = new Thickness(5, 4, 5, 4), CaretBrush = Brushes.White, VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static void DigitsOnly(TextBox box)
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

    private static Button MakeButton(string label, Action onClick, Brush? background = null)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 6, 14, 6),
            Foreground = Fg, Background = background ?? new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand, MinWidth = 74,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}

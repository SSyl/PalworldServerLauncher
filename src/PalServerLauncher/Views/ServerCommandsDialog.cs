using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.Rest.Models;
using static PalServerLauncher.Views.DarkControls;

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
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(0xC2, 0x42, 0x38));
    private static readonly Brush Warn = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x0B));

    private readonly ServerCommandActions _actions;
    private readonly Logger _logger;

    private readonly TextBox _announce;
    private readonly TextBox _reason;
    private readonly TextBox _unbanUserId;
    private readonly StackPanel _playersPanel;
    private readonly TextBlock _status;

    private ServerCommandsDialog(ServerCommandActions actions, Logger logger)
    {
        _actions = actions;
        _logger = logger;

        Title = Strings.ServerCmd_Title;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(Header(Strings.ServerCmd_HeaderAnnounce));
        _announce = Field("");
        _announce.AcceptsReturn = false;
        var announceRow = new Grid();
        announceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        announceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_announce, 0);
        var sendButton = MakeButton(Strings.ServerCmd_Send, OnSendAnnounce);
        sendButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(sendButton, 1);
        announceRow.Children.Add(_announce);
        announceRow.Children.Add(sendButton);
        stack.Children.Add(announceRow);

        var playersHeader = new Grid { Margin = new Thickness(0, 16, 0, 4) };
        playersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        playersHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var playersTitle = Header(Strings.ServerCmd_HeaderPlayers);
        playersTitle.Margin = new Thickness(0);
        Grid.SetColumn(playersTitle, 0);
        var refreshButton = MakeButton(Strings.ServerCmd_Refresh, OnRefreshPlayers);
        Grid.SetColumn(refreshButton, 1);
        playersHeader.Children.Add(playersTitle);
        playersHeader.Children.Add(refreshButton);
        stack.Children.Add(playersHeader);

        _reason = Field("");
        stack.Children.Add(Labeled(Strings.ServerCmd_ReasonLabel, _reason));

        _playersPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        _playersPanel.Children.Add(Line(Strings.ServerCmd_LoadingPlayers, Muted));
        stack.Children.Add(_playersPanel);

        stack.Children.Add(Header(Strings.ServerCmd_HeaderUnban));
        _unbanUserId = Field("");
        var unbanRow = new Grid();
        unbanRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        unbanRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_unbanUserId, 0);
        var unbanButton = MakeButton(Strings.ServerCmd_Unban, OnUnban);
        unbanButton.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(unbanButton, 1);
        unbanRow.Children.Add(_unbanUserId);
        unbanRow.Children.Add(unbanButton);
        stack.Children.Add(unbanRow);
        stack.Children.Add(Line(Strings.ServerCmd_UnbanHint, Muted));

        stack.Children.Add(Header(Strings.ServerCmd_HeaderServer));
        var serverRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        serverRow.Children.Add(MakeButton(Strings.ServerCmd_SaveWorld, OnSave));
        stack.Children.Add(serverRow);

        _status = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 0) };
        stack.Children.Add(_status);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(MakeButton(Strings.ServerCmd_Close, Close));
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
        if (message.Length == 0) { SetStatus(Strings.ServerCmd_EnterMessage); return; }
        SetStatus(await _actions.Announce(message) ? Strings.ServerCmd_Announced : Strings.ServerCmd_AnnounceFailed);
    });

    private async void OnRefreshPlayers() => await Guard(async () =>
    {
        _playersPanel.Children.Clear();
        _playersPanel.Children.Add(Line(Strings.ServerCmd_LoadingPlayers, Muted));

        var players = await _actions.GetPlayers();
        _playersPanel.Children.Clear();
        if (players is null) { _playersPanel.Children.Add(Line(Strings.ServerCmd_RestOff, Muted)); return; }
        if (players.Players.Count == 0) { _playersPanel.Children.Add(Line(Strings.ServerCmd_NoPlayers, Muted)); return; }

        foreach (var player in players.Players)
            _playersPanel.Children.Add(PlayerRow(player));
    });

    private async void OnUnban() => await Guard(async () =>
    {
        var userId = _unbanUserId.Text.Trim();
        if (userId.Length == 0) { SetStatus(Strings.ServerCmd_EnterUserId); return; }
        if (ChoiceDialog.Show(this, Strings.ServerCmd_UnbanTitle, string.Format(Strings.ServerCmd_UnbanConfirm, userId), Strings.ServerCmd_Unban, Strings.Common_Cancel) != 0) return;
        SetStatus(await _actions.Unban(userId) ? string.Format(Strings.ServerCmd_Unbanned, userId) : Strings.ServerCmd_UnbanFailed);
    });

    private async void OnSave() => await Guard(async () =>
        SetStatus(await _actions.Save() ? Strings.ServerCmd_WorldSaved : Strings.ServerCmd_SaveFailed));

    private Grid PlayerRow(Player player)
    {
        var name = player.Name ?? player.AccountName ?? Strings.ServerCmd_UnknownName;
        var grid = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Show the name and the platform user id (the thing that actually gets kicked/banned) so two players
        // with the same display name can be told apart.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock { Text = string.Format(Strings.ServerCmd_PlayerNameLevel, name, player.Level), Foreground = Fg });
        info.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(player.UserId) ? Strings.ServerCmd_NoUserId : player.UserId, Foreground = Muted, FontSize = 11 });
        Grid.SetColumn(info, 0);
        grid.Children.Add(info);

        var hasUserId = !string.IsNullOrEmpty(player.UserId);
        var kick = MakeButton(Strings.ServerCmd_Kick, () => OnKick(player), Warn);
        var ban = MakeButton(Strings.ServerCmd_Ban, () => OnBan(player), Danger);
        kick.IsEnabled = ban.IsEnabled = hasUserId;
        if (!hasUserId)
            kick.ToolTip = ban.ToolTip = Strings.ServerCmd_NoUserIdTooltip;
        Grid.SetColumn(kick, 1);
        Grid.SetColumn(ban, 2);
        grid.Children.Add(kick);
        grid.Children.Add(ban);
        return grid;
    }

    private async void OnKick(Player player) => await Guard(async () =>
    {
        var name = player.Name ?? player.AccountName ?? Strings.ServerCmd_UnknownName;
        if (string.IsNullOrEmpty(player.UserId)) return;
        if (ChoiceDialog.Show(this, Strings.ServerCmd_KickTitle, string.Format(Strings.ServerCmd_KickConfirm, name, player.UserId), Strings.ServerCmd_Kick, Strings.Common_Cancel) != 0) return;
        if (await _actions.Kick(player.UserId, _reason.Text.Trim()))
        {
            SetStatus(string.Format(Strings.ServerCmd_Kicked, name, player.UserId));
            OnRefreshPlayers();
        }
        else SetStatus(string.Format(Strings.ServerCmd_KickFailed, name));
    });

    private async void OnBan(Player player) => await Guard(async () =>
    {
        var name = player.Name ?? player.AccountName ?? Strings.ServerCmd_UnknownName;
        if (string.IsNullOrEmpty(player.UserId)) return;
        if (ChoiceDialog.Show(this, Strings.ServerCmd_BanTitle, string.Format(Strings.ServerCmd_BanConfirm, name, player.UserId), Strings.ServerCmd_Ban, Strings.Common_Cancel) != 0) return;
        if (await _actions.Ban(player.UserId, _reason.Text.Trim()))
        {
            SetStatus(string.Format(Strings.ServerCmd_Banned, name, player.UserId));
            OnRefreshPlayers();
        }
        else SetStatus(string.Format(Strings.ServerCmd_BanFailed, name));
    });

    private async Task Guard(Func<Task> body)
    {
        try { await body(); }
        catch (Exception ex)
        {
            _logger.Error("Server command failed", ex);
            SetStatus(Strings.ServerCmd_CommandFailed);
        }
    }

    private void SetStatus(string text) => _status.Text = text;

    // --- small dark-theme builders (mirrors PortCheckDialog / DiscordDialog) ---
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

}

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using PalServerLauncher.Logging;
using PalServerLauncher.Rcon;
using PalServerLauncher.Rest.Models;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Live server commands over the REST API: announce, kick / ban an online player (with a reason), unban by
/// user id, save, shutdown with an in-game countdown, and force stop. Opened only while the server is up.
/// Destructive actions (kick / ban / shutdown / force stop) confirm through <see cref="ChoiceDialog"/> first.
/// Built in code with the async handler pattern from <see cref="PortCheckDialog"/> (handlers are
/// <c>async void</c>, guarded so a REST hiccup can't crash the app).
///
/// When RCON is enabled in the ini, the content splits into REST and RCON tabs, the RCON tab being a barebones
/// raw console (send a command, see the response) over <see cref="RconClient"/>. RCON is deprecated by Palworld,
/// so it's kept isolated: nothing here depends on it and a failed connect just prints an error.
/// </summary>
public sealed class ServerCommandsDialog : Window
{
    private static readonly Brush Danger = Theme.Danger;
    private static readonly Brush Warn = Theme.Warning;

    private readonly ServerCommandActions _actions;
    private readonly Logger _logger;
    private readonly RconConnectionInfo _rcon;

    private readonly TextBox _announce;
    private readonly TextBox _reason;
    private readonly TextBox _unbanUserId;
    private readonly StackPanel _playersPanel;
    private readonly TextBlock _status;

    // RCON tab (only built when _rcon.Enabled).
    private const int MaxTerminalLines = 500;
    // The console transcript. Static so it survives closing and reopening the window within a session, seeded
    // once per session from rcon-log.txt and saved back on every line, and bounded to the last MaxTerminalLines
    // both in memory and on disk so it can't grow without end.
    private static readonly List<string> _rconTranscript = new();
    private static bool _rconTranscriptLoaded;
    private readonly string _rconHistoryPath = Path.Combine(LauncherConfig.DataRoot, "rcon-history.json");
    private readonly string _rconLogPath = Path.Combine(LauncherConfig.DataRoot, "rcon-log.txt");
    private TextBox? _rconTerminal;
    private TextBox? _rconInput;
    private RconClient? _rconClient;
    private List<string> _rconHistory = new();

    private ServerCommandsDialog(ServerCommandActions actions, RconConnectionInfo rcon, bool restReady, Logger logger)
    {
        _actions = actions;
        _rcon = rcon;
        _logger = logger;

        Title = Strings.ServerCmd_Title;
        Background = Theme.Window;
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

        // The REST commands need the REST API. When it isn't connected, gray the controls and explain why (the
        // banner-over-disabled-controls pattern the Server Settings dialog uses while the server is running).
        FrameworkElement restContent;
        if (restReady)
        {
            restContent = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }
        else
        {
            stack.IsEnabled = false;
            var banner = Banner(Strings.ServerCmd_RestUnavailable);
            banner.Margin = new Thickness(18, 18, 18, 0);
            DockPanel.SetDock(banner, Dock.Top);
            var restDock = new DockPanel();
            restDock.Children.Add(banner);
            restDock.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            restContent = restDock;
        }

        // Body is the REST content alone, or REST | RCON tabs when RCON is enabled in the ini.
        FrameworkElement body;
        if (_rcon.Enabled)
        {
            _rconHistory = RconHistory.Load(_rconHistoryPath);
            if (!_rconTranscriptLoaded)
            {
                _rconTranscriptLoaded = true;
                _rconTranscript.AddRange(RconTranscript.Load(_rconLogPath));
                CapTranscript();
            }
            var tabs = new TabControl { Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            if (Application.Current?.TryFindResource("DarkTabItem") is Style tabStyle)
                tabs.ItemContainerStyle = tabStyle;
            tabs.Items.Add(new TabItem { Header = Strings.ServerCmd_TabRest, Content = restContent });
            tabs.Items.Add(new TabItem { Header = Strings.ServerCmd_TabRcon, Content = BuildRconTab() });
            body = tabs;
            Closed += (_, _) => _rconClient?.Dispose();
        }
        else
        {
            body = restContent;
        }

        // One shared Close footer, kept out of the (disable-able) REST stack so it stays clickable.
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(18, 8, 18, 12) };
        footer.Children.Add(MakeButton(Strings.ServerCmd_Close, Close));
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(footer);
        root.Children.Add(body);
        Content = root;

        Loaded += (_, _) => OnRefreshPlayers();
    }

    public static void Show(Window? owner, ServerCommandActions actions, RconConnectionInfo rcon, bool restReady, Logger logger)
    {
        var dialog = new ServerCommandsDialog(actions, rcon, restReady, logger) { Owner = owner };
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

    // --- RCON tab (barebones raw console; only built when _rcon.Enabled) ---

    private FrameworkElement BuildRconTab()
    {
        var panel = new DockPanel { Margin = new Thickness(18) };

        var blurb = Banner(Strings.ServerCmd_RconDeprecated); // amber notice box, matching the CPU Affinity/Priority tab
        blurb.Margin = new Thickness(0, 0, 0, 10);
        DockPanel.SetDock(blurb, Dock.Top);
        panel.Children.Add(blurb);

        var inputRow = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _rconInput = Field("");
        _rconInput.KeyDown += (_, e) => { if (e.Key == Key.Enter) OnRconSend(); };
        Grid.SetColumn(_rconInput, 0);

        var sendButton = IconButton("", Strings.ServerCmd_Send, OnRconSend); // MDL2 "Send"
        Grid.SetColumn(sendButton, 1);

        var historyButton = IconButton("", Strings.ServerCmd_RconRecent, null); // MDL2 "History"
        historyButton.Click += (_, _) => ShowHistoryMenu(historyButton);
        Grid.SetColumn(historyButton, 2);

        inputRow.Children.Add(_rconInput);
        inputRow.Children.Add(sendButton);
        inputRow.Children.Add(historyButton);
        DockPanel.SetDock(inputRow, Dock.Bottom);
        panel.Children.Add(inputRow);

        var terminal = new TextBox
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = false, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"),
            Background = Theme.Sunken, Foreground = Fg, BorderBrush = FieldBorder, BorderThickness = new Thickness(1),
            Padding = new Thickness(6), Height = 260,
            Text = string.Join("\n", _rconTranscript), // restore this session's earlier output
        };
        terminal.Loaded += (_, _) => terminal.ScrollToEnd(); // show the latest line when the tab first opens
        _rconTerminal = terminal;
        panel.Children.Add(terminal); // fills between the blurb and the input row
        return panel;
    }

    /// <summary>An icon-only dark button using a Segoe MDL2 Assets glyph, with a tooltip standing in for the label.</summary>
    private static Button IconButton(string glyph, string tooltip, Action? onClick)
    {
        var button = new Button
        {
            Content = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 15,
            Margin = new Thickness(8, 0, 0, 0), MinWidth = 40, ToolTip = tooltip,
        };
        if (onClick is not null)
            button.Click += (_, _) => onClick();
        return button;
    }

    private async void OnRconSend()
    {
        if (_rconInput is null || _rconTerminal is null)
            return;

        var command = _rconInput.Text.Trim();
        if (command.Length == 0)
            return;
        _rconInput.Clear();
        AppendTerminal($"> {command}");
        RememberCommand(command);

        try
        {
            if (!await EnsureRconConnectedAsync())
                return;
            var response = (await _rconClient!.ExecuteAsync(command)).TrimEnd();
            if (response.Length > 0)
                AppendTerminal(response);
        }
        catch (Exception ex)
        {
            _logger.Error("RCON command failed", ex);
            AppendTerminal(Strings.ServerCmd_RconError);
            DropRcon(); // force a fresh connect on the next command
        }
    }

    private async Task<bool> EnsureRconConnectedAsync()
    {
        if (_rconClient is { IsConnected: true })
            return true;

        var target = $"{_rcon.Host}:{_rcon.Port}";
        AppendTerminal(string.Format(Strings.ServerCmd_RconConnecting, target));

        DropRcon();
        _rconClient = new RconClient(_rcon.Host, _rcon.Port, _rcon.Password);
        var result = await _rconClient.ConnectAsync();
        switch (result)
        {
            case RconConnectResult.Connected:
                AppendTerminal(string.Format(Strings.ServerCmd_RconConnected, target));
                return true;
            case RconConnectResult.AuthFailed:
                AppendTerminal(Strings.ServerCmd_RconAuthFailed);
                break;
            case RconConnectResult.Unreachable:
                AppendTerminal(string.Format(Strings.ServerCmd_RconUnreachable, target));
                break;
            default:
                AppendTerminal(Strings.ServerCmd_RconError);
                break;
        }
        DropRcon();
        return false;
    }

    private void AppendTerminal(string text)
    {
        // Timestamp the entry's first line; align continuation lines of a multi-line response under it.
        var stamp = $"[{DateTime.Now:HH:mm:ss}] ";
        var indent = new string(' ', stamp.Length);
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
            _rconTranscript.Add((i == 0 ? stamp : indent) + lines[i]);
        CapTranscript();
        RconTranscript.Save(_rconLogPath, _rconTranscript);

        if (_rconTerminal is null)
            return;
        _rconTerminal.Text = string.Join("\n", _rconTranscript);
        _rconTerminal.ScrollToEnd();
    }

    private static void CapTranscript()
    {
        if (_rconTranscript.Count > MaxTerminalLines)
            _rconTranscript.RemoveRange(0, _rconTranscript.Count - MaxTerminalLines);
    }

    private void RememberCommand(string command)
    {
        _rconHistory = RconHistory.Add(_rconHistory, command);
        RconHistory.Save(_rconHistoryPath, _rconHistory);
    }

    // A dropdown of recent commands. Clicking one fills the input (it doesn't run it). The menu is themed by the
    // app-wide ContextMenu / MenuItem styles in App.xaml, so there's no unthemed icon gutter.
    private void ShowHistoryMenu(Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        if (_rconHistory.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Strings.ServerCmd_RconNoHistory, IsEnabled = false });
        }
        else
        {
            foreach (var command in _rconHistory)
            {
                var item = new MenuItem { Header = command };
                item.Click += (_, _) =>
                {
                    if (_rconInput is null) return;
                    _rconInput.Text = command;
                    _rconInput.CaretIndex = command.Length;
                    _rconInput.Focus();
                };
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    private void DropRcon()
    {
        _rconClient?.Dispose();
        _rconClient = null;
    }

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

    /// <summary>The amber notice box, matching the Server Settings dialog (its "server running" banner style).</summary>
    private static Border Banner(string text) => new()
    {
        Background = Theme.BannerBg,
        Padding = new Thickness(10, 8, 10, 8),
        Child = new TextBlock { Text = text, Foreground = Theme.BannerFg, TextWrapping = TextWrapping.Wrap },
    };

}

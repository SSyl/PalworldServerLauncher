using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;

namespace PalServerLauncher.Views;

/// <summary>
/// Configures the Discord integration: the control bot (slash commands from one locked-down channel) and
/// the outbound webhook notifications. Writes the config and returns true if the user saved (the caller
/// then reconnects the bot). Built in code to match the other dark dialogs.
/// </summary>
public sealed class DiscordDialog : Window
{
    private static readonly Brush Fg = Brushes.White;
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
    private static readonly Brush FieldBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly Brush FieldBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
    private static readonly Brush LinkFg = new SolidColorBrush(Color.FromRgb(0x5A, 0xA0, 0xE0));

    private readonly LauncherConfig _config;
    private bool _saved;

    private readonly CheckBox _botEnabled;
    private readonly SecretField _token;
    private readonly TextBox _channelId;
    private readonly TextBox _roleId;
    private readonly TextBox _cooldown;
    private readonly CheckBox _notifyEnabled;
    private readonly TextBox _webhook;
    private readonly CheckBox _notifyLifecycle;
    private readonly CheckBox _notifyPlayers;
    private readonly Dictionary<string, CheckBox> _commandChecks = new();

    private DiscordDialog(LauncherConfig config)
    {
        _config = config;

        Title = "Discord";
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(Blurb(
            "Control the server from Discord with slash commands, and/or post lifecycle notifications to a "
            + "channel. Create a bot and copy its token from the",
            "https://discord.com/developers/applications", "Discord Developer Portal",
            " (add a Bot, then invite it with the bot + applications.commands scopes)."));

        stack.Children.Add(Header("Server control bot"));
        _botEnabled = Check("Enable the control bot (slash commands: /status /players /save /backup /update /start /restart /stop)", config.DiscordBotEnabled);
        stack.Children.Add(_botEnabled);
        _token = new SecretField(config.DiscordBotToken, editable: true);
        stack.Children.Add(Row("Bot token (secret)", _token.Element));
        _channelId = Field(config.DiscordCommandChannelId == 0 ? "" : config.DiscordCommandChannelId.ToString(CultureInfo.InvariantCulture));
        DigitsOnly(_channelId);
        stack.Children.Add(Row("Command channel ID", _channelId));
        _roleId = Field(config.DiscordCommandRoleId == 0 ? "" : config.DiscordCommandRoleId.ToString(CultureInfo.InvariantCulture));
        DigitsOnly(_roleId);
        stack.Children.Add(Row("Required role ID (optional)", _roleId));
        _cooldown = Field(config.DiscordCommandCooldownSeconds.ToString(CultureInfo.InvariantCulture));
        DigitsOnly(_cooldown);
        stack.Children.Add(Row("Command cooldown (seconds)", _cooldown));
        stack.Children.Add(Note(
            "Security: set a command channel and/or a required role - the bot only acts when every gate you set "
            + "passes (set at least one). Anyone who can post in the command channel, or has the role, can "
            + "control the server, so lock it down (a private, admin-only channel and/or an admin role). Enable "
            + "Discord Developer Mode, then right-click a channel or role -> Copy ID. The token is stored locally "
            + "in launcher.json and is never logged."));

        stack.Children.Add(Header("Commands the bot exposes"));
        stack.Children.Add(new TextBlock
        {
            Text = "Tick the slash commands the bot should accept. Destructive ones (restart, stop, kick, ban) are "
                 + "off by default. Changes take effect on Save (the bot reconnects and re-registers its commands).",
            Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2),
        });
        var commandsPanel = new WrapPanel();
        foreach (var command in DiscordBotService.AllCommands)
        {
            var check = new CheckBox
            {
                Content = "/" + command.Name,
                IsChecked = DiscordBotService.IsCommandEnabled(config, command.Name),
                Foreground = Fg, Width = 150, Margin = new Thickness(0, 4, 12, 0),
                ToolTip = command.Description,
            };
            _commandChecks[command.Name] = check;
            commandsPanel.Children.Add(check);
        }
        stack.Children.Add(commandsPanel);

        stack.Children.Add(Header("Webhook notifications"));
        _notifyEnabled = Check("Enable notifications", config.DiscordEnabled);
        stack.Children.Add(_notifyEnabled);
        _webhook = Field(config.DiscordWebhookUrl);
        stack.Children.Add(Row("Webhook URL", _webhook));
        _notifyLifecycle = Check("Notify on up / down / update / crash", config.DiscordNotifyLifecycle);
        stack.Children.Add(_notifyLifecycle);
        _notifyPlayers = Check("Notify on player join / leave (needs REST)", config.DiscordNotifyPlayers);
        stack.Children.Add(_notifyPlayers);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(MakeButton("Save", OnSave));
        buttons.Children.Add(MakeButton("Cancel", Close));
        stack.Children.Add(buttons);

        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static bool Show(Window? owner, LauncherConfig config)
    {
        var dialog = new DiscordDialog(config) { Owner = owner };
        dialog.ShowDialog();
        return dialog._saved;
    }

    private void OnSave()
    {
        ulong channelId = 0;
        var channelText = _channelId.Text.Trim();
        if (channelText.Length > 0 && !ulong.TryParse(channelText, NumberStyles.None, CultureInfo.InvariantCulture, out channelId))
        {
            ChoiceDialog.Show(this, "Invalid channel ID",
                "The command channel ID must be a number (Discord Developer Mode -> right-click the channel -> Copy Channel ID).", "OK");
            return;
        }
        ulong roleId = 0;
        var roleText = _roleId.Text.Trim();
        if (roleText.Length > 0 && !ulong.TryParse(roleText, NumberStyles.None, CultureInfo.InvariantCulture, out roleId))
        {
            ChoiceDialog.Show(this, "Invalid role ID",
                "The required role ID must be a number (Discord Developer Mode -> right-click the role -> Copy Role ID).", "OK");
            return;
        }
        if (_botEnabled.IsChecked == true && (_token.Value.Trim().Length == 0 || (channelId == 0 && roleId == 0)))
        {
            ChoiceDialog.Show(this, "Bot not ready",
                "To enable the control bot, set a bot token and at least one gate: a command channel ID and/or a required role ID.", "OK");
            return;
        }

        _config.DiscordBotEnabled = _botEnabled.IsChecked == true;
        _config.DiscordBotToken = _token.Value.Trim();
        _config.DiscordCommandChannelId = channelId;
        _config.DiscordCommandRoleId = roleId;
        _config.DiscordCommandCooldownSeconds = int.TryParse(_cooldown.Text.Trim(), out var cd) ? Math.Max(0, cd) : 5;
        _config.DiscordEnabled = _notifyEnabled.IsChecked == true;
        _config.DiscordWebhookUrl = _webhook.Text.Trim();
        _config.DiscordNotifyLifecycle = _notifyLifecycle.IsChecked == true;
        _config.DiscordNotifyPlayers = _notifyPlayers.IsChecked == true;
        foreach (var (name, check) in _commandChecks)
            _config.DiscordCommandEnabled[name] = check.IsChecked == true;
        _config.Save();
        _saved = true;
        Close();
    }

    // --- small dark-theme builders ---
    private static TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Fg, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 18, 0, 6),
    };

    private static Grid Row(string label, FrameworkElement input)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, Foreground = Fg, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(input, 1);
        grid.Children.Add(text);
        grid.Children.Add(input);
        return grid;
    }

    private static TextBox Field(string value) => new()
    {
        Text = value, Background = FieldBg, Foreground = Fg, BorderBrush = FieldBorder,
        Padding = new Thickness(5, 4, 5, 4), CaretBrush = Brushes.White,
    };

    private static CheckBox Check(string text, bool value) => new()
    {
        Content = text, IsChecked = value, Foreground = Fg, Margin = new Thickness(0, 6, 0, 0),
    };

    private static Border Note(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x2E, 0x1E)),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 8, 0, 0),
        Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xC0, 0x80)), TextWrapping = TextWrapping.Wrap },
    };

    private static TextBlock Blurb(string prefix, string url, string linkText, string suffix)
    {
        var block = new TextBlock { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
        block.Inlines.Add(prefix + " ");
        var link = new Hyperlink(new Run(linkText)) { NavigateUri = new Uri(url), Foreground = LinkFg };
        link.RequestNavigate += (_, e) => { OpenUrl(e.Uri.AbsoluteUri); e.Handled = true; };
        block.Inlines.Add(link);
        block.Inlines.Add(suffix);
        return block;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No default browser / launch blocked - nothing useful to do here.
        }
    }

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

    private static Button MakeButton(string label, Action onClick)
    {
        var button = new Button
        {
            Content = label, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(16, 7, 16, 7),
            Foreground = Fg, Background = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, MinWidth = 90,
        };
        button.Click += (_, _) => onClick();
        return button;
    }
}

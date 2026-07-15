using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PalServerLauncher.Config;
using PalServerLauncher.Core;
using PalServerLauncher.Localization;
using static PalServerLauncher.Views.DarkControls;

namespace PalServerLauncher.Views;

/// <summary>
/// Configures the Discord integration: the control bot (slash commands from one locked-down channel) and
/// the outbound webhook notifications. Writes the config and returns true if the user saved (the caller
/// then reconnects the bot). Built in code to match the other dark dialogs.
/// </summary>
public sealed class DiscordDialog : Window
{
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

        Title = Strings.Discord_Title;
        Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ShowInTaskbar = false;

        var stack = new StackPanel { Margin = new Thickness(18) };

        stack.Children.Add(Blurb(
            Strings.Discord_BlurbPrefix,
            "https://discord.com/developers/applications", Strings.Discord_BlurbLinkText,
            Strings.Discord_BlurbSuffix));

        stack.Children.Add(Header(Strings.Discord_ServerControlBotHeader));
        _botEnabled = Check(Strings.Discord_EnableControlBot, config.DiscordBotEnabled);
        stack.Children.Add(_botEnabled);
        _token = new SecretField(config.DiscordBotToken, editable: true);
        stack.Children.Add(Row(Strings.Discord_BotTokenLabel, _token.Element));
        _channelId = Field(config.DiscordCommandChannelId == 0 ? "" : config.DiscordCommandChannelId.ToString(CultureInfo.InvariantCulture));
        DigitsOnly(_channelId);
        stack.Children.Add(Row(Strings.Discord_CommandChannelIdLabel, _channelId));
        _roleId = Field(config.DiscordCommandRoleId == 0 ? "" : config.DiscordCommandRoleId.ToString(CultureInfo.InvariantCulture));
        DigitsOnly(_roleId);
        stack.Children.Add(Row(Strings.Discord_RequiredRoleIdLabel, _roleId));
        _cooldown = Field(config.DiscordCommandCooldownSeconds.ToString(CultureInfo.InvariantCulture));
        DigitsOnly(_cooldown);
        stack.Children.Add(Row(Strings.Discord_CommandCooldownLabel, _cooldown));
        stack.Children.Add(Note(Strings.Discord_SecurityNote));

        stack.Children.Add(Header(Strings.Discord_CommandsExposedHeader));
        stack.Children.Add(new TextBlock
        {
            Text = Strings.Discord_CommandsExposedHint,
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

        stack.Children.Add(Header(Strings.Discord_WebhookNotificationsHeader));
        _notifyEnabled = Check(Strings.Discord_EnableNotifications, config.DiscordEnabled);
        stack.Children.Add(_notifyEnabled);
        _webhook = Field(config.DiscordWebhookUrl);
        stack.Children.Add(Row(Strings.Discord_WebhookUrlLabel, _webhook));
        _notifyLifecycle = Check(Strings.Discord_NotifyLifecycle, config.DiscordNotifyLifecycle);
        stack.Children.Add(_notifyLifecycle);
        _notifyPlayers = Check(Strings.Discord_NotifyPlayers, config.DiscordNotifyPlayers);
        stack.Children.Add(_notifyPlayers);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(MakeButton(Strings.Common_Save, OnSave));
        buttons.Children.Add(MakeButton(Strings.Common_Cancel, Close));
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
            ChoiceDialog.Show(this, Strings.Discord_InvalidChannelIdTitle,
                Strings.Discord_InvalidChannelIdMessage, Strings.Common_OK);
            return;
        }
        ulong roleId = 0;
        var roleText = _roleId.Text.Trim();
        if (roleText.Length > 0 && !ulong.TryParse(roleText, NumberStyles.None, CultureInfo.InvariantCulture, out roleId))
        {
            ChoiceDialog.Show(this, Strings.Discord_InvalidRoleIdTitle,
                Strings.Discord_InvalidRoleIdMessage, Strings.Common_OK);
            return;
        }
        if (_botEnabled.IsChecked == true && (_token.Value.Trim().Length == 0 || (channelId == 0 && roleId == 0)))
        {
            ChoiceDialog.Show(this, Strings.Discord_BotNotReadyTitle,
                Strings.Discord_BotNotReadyMessage, Strings.Common_OK);
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
}

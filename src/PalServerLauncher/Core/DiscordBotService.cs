using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using PalServerLauncher.Config;
using PalServerLauncher.Logging;

namespace PalServerLauncher.Core;

/// <summary>
/// Optional Discord bot (Gateway) that runs server commands from ONE locked-down Discord channel. It runs
/// inside the launcher (dials out - no inbound ports) and is active only while the launcher is running,
/// like the webhook notifier. Auth is the command channel's Discord permissions: the bot only acts on the
/// configured channel, so anyone who can post there is trusted (the launcher keeps no user allowlist).
/// Slash commands only (<see cref="GatewayIntents.Guilds"/>) - the bot never reads general chat - and the
/// token is never logged. This build handles the read-only commands (/status, /players); control commands
/// come next. All server work is delegated to <see cref="DiscordCommands"/> so routing stays testable.
/// </summary>
public sealed class DiscordBotService : IDisposable
{
    /// <summary>Operations the bot can invoke; each returns a short user-facing status string.</summary>
    public sealed record DiscordCommands(
        Func<Task<string>> Status,
        Func<Task<string>> Players,
        Func<Task<string>> Save,
        Func<Task<string>> Backup,
        Func<Task<string>> Restart,
        Func<Task<string>> Stop,
        Func<Task<string>> Start,
        Func<Task<string>> Update);

    // Commands that take the server down / bounce it require a confirm click before they run.
    private static readonly HashSet<string> DestructiveCommands = new() { "restart", "stop" };
    private const string ButtonPrefix = "palcmd:";

    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly DiscordCommands _commands;
    private readonly DiscordCommandCooldown _cooldown = new();
    private DiscordSocketClient? _client;
    private bool _disposed;

    public DiscordBotService(LauncherConfig config, Logger logger, DiscordCommands commands)
    {
        _config = config;
        _logger = logger;
        _commands = commands;
    }

    /// <summary>Connect if enabled and a token is set. No-op when already connected or disabled.</summary>
    public async Task StartAsync()
    {
        if (_disposed || _client is not null)
            return;
        if (!_config.DiscordBotEnabled || string.IsNullOrWhiteSpace(_config.DiscordBotToken))
            return;

        var client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds, // slash commands only - no message content
            LogLevel = LogSeverity.Warning,
        });
        client.Log += OnClientLog;
        client.Ready += OnReadyAsync;
        client.SlashCommandExecuted += OnSlashCommandAsync;
        client.ButtonExecuted += OnButtonAsync;
        _client = client;

        try
        {
            await client.LoginAsync(TokenType.Bot, _config.DiscordBotToken).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            _logger.Info("Discord bot connecting...");
        }
        catch (Exception ex)
        {
            _logger.Error("Discord bot failed to connect - check the bot token", ex);
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Disconnect and tear down (on disable / shutdown).</summary>
    public async Task StopAsync()
    {
        var client = _client;
        _client = null;
        if (client is null)
            return;
        try
        {
            await client.StopAsync().ConfigureAwait(false);
            await client.LogoutAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.Debug($"Discord bot stop: {ex.Message}");
        }
        client.Dispose();
    }

    /// <summary>Reconnect with the current settings (call after the Discord settings change).</summary>
    public async Task ReconfigureAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await StartAsync().ConfigureAwait(false);
    }

    private async Task OnReadyAsync()
    {
        if (_client is null)
            return;

        // Register commands per-guild (instant; global commands take ~1h to propagate).
        var commands = new ApplicationCommandProperties[]
        {
            new SlashCommandBuilder().WithName("status").WithDescription("Show server status (FPS, players, uptime, version).").Build(),
            new SlashCommandBuilder().WithName("players").WithDescription("List players currently online.").Build(),
            new SlashCommandBuilder().WithName("save").WithDescription("Save the world now.").Build(),
            new SlashCommandBuilder().WithName("backup").WithDescription("Take a backup now.").Build(),
            new SlashCommandBuilder().WithName("update").WithDescription("Check for a server update (does not apply it).").Build(),
            new SlashCommandBuilder().WithName("start").WithDescription("Start the server.").Build(),
            new SlashCommandBuilder().WithName("restart").WithDescription("Restart the server (asks for confirmation).").Build(),
            new SlashCommandBuilder().WithName("stop").WithDescription("Stop the server (asks for confirmation).").Build(),
        };
        foreach (var guild in _client.Guilds)
        {
            try { await guild.BulkOverwriteApplicationCommandAsync(commands).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Debug($"Discord command registration failed for guild {guild.Id}: {ex.Message}"); }
        }
        _logger.Info($"Discord bot ready as {_client.CurrentUser?.Username} (commands registered in {_client.Guilds.Count} server(s)).");
    }

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!IsAuthorized(command))
        {
            await command.RespondAsync("You're not allowed to run commands here.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!AllowNow(command.User.Id, out var retryAfter))
        {
            await command.RespondAsync($"Slow down - try again in {retryAfter.TotalSeconds:F0}s.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (Resolve(command.CommandName) is null)
        {
            await command.RespondAsync("Unknown command.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        // Destructive commands ask first. The prompt is ephemeral, so only the invoker can click Confirm.
        if (DestructiveCommands.Contains(command.CommandName))
        {
            var buttons = new ComponentBuilder()
                .WithButton("Confirm", ButtonPrefix + command.CommandName, ButtonStyle.Danger)
                .WithButton("Cancel", ButtonPrefix + "cancel", ButtonStyle.Secondary)
                .Build();
            await command.RespondAsync($"⚠️ Really **/{command.CommandName}** the server?", components: buttons, ephemeral: true).ConfigureAwait(false);
            return;
        }

        // Post the result publicly in the channel (AllowedMentions.None so an echoed name can't ping).
        await command.DeferAsync().ConfigureAwait(false);
        var result = await RunAsync(command.CommandName, command.User.Username).ConfigureAwait(false);
        await command.FollowupAsync(result, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        if (!id.StartsWith(ButtonPrefix, StringComparison.Ordinal))
            return;
        var action = id[ButtonPrefix.Length..];

        if (!IsAuthorized(component))
        {
            await component.RespondAsync("You're not allowed to run commands here.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (action == "cancel")
        {
            await ClearWith(component, "Cancelled.").ConfigureAwait(false);
            return;
        }

        // Clear the (ephemeral) confirm prompt for the clicker, then post the result publicly in the channel.
        await ClearWith(component, $"Confirmed - running /{action}.").ConfigureAwait(false);
        var result = await RunAsync(action, component.User.Username).ConfigureAwait(false);
        await component.FollowupAsync(result, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(string action, string user)
    {
        var handler = Resolve(action);
        if (handler is null)
            return "Unknown command.";
        try
        {
            var result = await handler().ConfigureAwait(false);
            _logger.Info($"Discord command /{action} by {user}.");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error($"Discord command /{action} failed", ex);
            return "That command failed - check the launcher log.";
        }
    }

    private Func<Task<string>>? Resolve(string name) => name switch
    {
        "status" => _commands.Status,
        "players" => _commands.Players,
        "save" => _commands.Save,
        "backup" => _commands.Backup,
        "update" => _commands.Update,
        "start" => _commands.Start,
        "restart" => _commands.Restart,
        "stop" => _commands.Stop,
        _ => null,
    };

    /// <summary>Allowed only if every configured gate passes (channel AND role) and at least one is set,
    /// so an unconfigured bot never becomes wide-open (fail closed).</summary>
    private bool IsAuthorized(SocketInteraction interaction)
    {
        var channelSet = _config.DiscordCommandChannelId != 0;
        var roleSet = _config.DiscordCommandRoleId != 0;
        if (!channelSet && !roleSet)
            return false;

        var channelOk = !channelSet || interaction.ChannelId == _config.DiscordCommandChannelId;
        var roleOk = !roleSet || HasRole(interaction.User, _config.DiscordCommandRoleId);
        return channelOk && roleOk;
    }

    private static bool HasRole(SocketUser user, ulong roleId) =>
        user is SocketGuildUser member && member.Roles.Any(r => r.Id == roleId);

    private bool AllowNow(ulong userId, out TimeSpan retryAfter) =>
        _cooldown.TryUse(userId, DateTime.UtcNow, TimeSpan.FromSeconds(Math.Max(0, _config.DiscordCommandCooldownSeconds)), out retryAfter);

    private static Task ClearWith(SocketMessageComponent component, string content) =>
        component.UpdateAsync(m => { m.Content = content; m.Components = new ComponentBuilder().Build(); });

    private Task OnClientLog(LogMessage log)
    {
        _logger.Debug($"Discord: {log.Message ?? log.Exception?.Message}");
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ = StopAsync();
    }
}

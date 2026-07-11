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
/// inside the launcher (dials out, no inbound ports) and is active only while the launcher is running,
/// like the webhook notifier. Auth is the command channel's Discord permissions: the bot only acts on the
/// configured channel, so anyone who can post there is trusted (the launcher keeps no user allowlist).
/// Slash commands only (<see cref="GatewayIntents.Guilds"/>), the bot never reads general chat, and the
/// token is never logged. Which commands are exposed is admin-configurable (see <see cref="IsCommandEnabled"/>),
/// destructive ones default off, and only enabled commands are registered. All server work is delegated to
/// <see cref="DiscordCommands"/> so routing stays testable.
/// </summary>
public sealed class DiscordBotService : IDisposable
{
    /// <summary>Operations the bot can invoke; each returns a short user-facing status string. The last four
    /// take arguments supplied as slash-command options.</summary>
    public sealed record DiscordCommands(
        Func<Task<string>> Status,
        Func<Task<string>> Players,
        Func<Task<string>> Save,
        Func<Task<string>> Backup,
        Func<Task<string>> Restart,
        Func<Task<string>> Stop,
        Func<Task<string>> Start,
        Func<Task<string>> Update,
        Func<string, Task<string>> Announce,
        Func<string, string, Task<string>> Kick,
        Func<string, string, Task<string>> Ban,
        Func<string, Task<string>> Unban,
        Func<string, Task<string?>> ResolvePlayerName);

    // Commands that take the server down / bounce it, or moderate a player, require a confirm click first.
    private static readonly HashSet<string> DestructiveCommands = new() { "restart", "stop", "kick", "ban" };
    private const string ButtonPrefix = "palcmd:";

    public sealed record CommandInfo(string Name, bool DefaultEnabled, string Description);

    /// <summary>Every bot command with its built-in default exposure and a short description (for the toggle UI).
    /// Reads and benign actions default on; destructive ones (restart / stop / kick / ban) default off.</summary>
    public static readonly IReadOnlyList<CommandInfo> AllCommands = new[]
    {
        new CommandInfo("status", true, "Show server status"),
        new CommandInfo("players", true, "List online players"),
        new CommandInfo("save", true, "Save the world"),
        new CommandInfo("backup", true, "Take a backup"),
        new CommandInfo("update", true, "Check for a server update"),
        new CommandInfo("announce", true, "Broadcast a message"),
        new CommandInfo("start", true, "Start the server"),
        new CommandInfo("unban", true, "Unban a player"),
        new CommandInfo("restart", false, "Restart the server"),
        new CommandInfo("stop", false, "Stop the server"),
        new CommandInfo("kick", false, "Kick a player"),
        new CommandInfo("ban", false, "Ban a player"),
    };

    /// <summary>Whether a command is exposed: the configured value, or its built-in default if unset (unknown -> off).</summary>
    public static bool IsCommandEnabled(LauncherConfig config, string name)
    {
        if (config.DiscordCommandEnabled.TryGetValue(name, out var enabled))
            return enabled;
        return AllCommands.FirstOrDefault(c => c.Name == name)?.DefaultEnabled ?? false;
    }

    private readonly LauncherConfig _config;
    private readonly Logger _logger;
    private readonly DiscordCommands _commands;
    private readonly DiscordCommandCooldown _cooldown = new();
    private readonly Dictionary<string, Pending> _pending = new();
    private readonly object _pendingGate = new();
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(5);
    private DiscordSocketClient? _client;
    private bool _disposed;

    /// <summary>A destructive command awaiting its Confirm click, holding the arguments a button can't carry.</summary>
    private sealed record Pending(string Action, string[] Args, DateTime CreatedUtc);

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
            _logger.Error("Discord bot failed to connect, check the bot token", ex);
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

        // Register only the enabled commands, per-guild (instant; global commands take ~1h to propagate).
        var commands = BuildCommandDefinitions()
            .Where(definition => IsCommandEnabled(_config, definition.Name))
            .Select(definition => definition.Props)
            .ToArray();

        foreach (var guild in _client.Guilds)
        {
            try { await guild.BulkOverwriteApplicationCommandAsync(commands).ConfigureAwait(false); }
            catch (Exception ex) { _logger.Debug($"Discord command registration failed for guild {guild.Id}: {ex.Message}"); }
        }
        _logger.Info($"Discord bot ready as {_client.CurrentUser?.Username} ({commands.Length} command(s) in {_client.Guilds.Count} server(s)).");
    }

    private static IReadOnlyList<(string Name, ApplicationCommandProperties Props)> BuildCommandDefinitions() => new (string, ApplicationCommandProperties)[]
    {
        ("status", new SlashCommandBuilder().WithName("status").WithDescription("Show server status (FPS, players, uptime, version).").Build()),
        ("players", new SlashCommandBuilder().WithName("players").WithDescription("List players currently online.").Build()),
        ("save", new SlashCommandBuilder().WithName("save").WithDescription("Save the world now.").Build()),
        ("backup", new SlashCommandBuilder().WithName("backup").WithDescription("Take a backup now.").Build()),
        ("update", new SlashCommandBuilder().WithName("update").WithDescription("Check for a server update (does not apply it).").Build()),
        ("announce", new SlashCommandBuilder().WithName("announce").WithDescription("Broadcast a message to the server.")
            .AddOption("message", ApplicationCommandOptionType.String, "The message to broadcast", isRequired: true).Build()),
        ("start", new SlashCommandBuilder().WithName("start").WithDescription("Start the server.").Build()),
        ("unban", new SlashCommandBuilder().WithName("unban").WithDescription("Unban a player by user id.")
            .AddOption("userid", ApplicationCommandOptionType.String, "Platform user id, e.g. steam_0123...", isRequired: true).Build()),
        ("restart", new SlashCommandBuilder().WithName("restart").WithDescription("Restart the server (asks for confirmation).").Build()),
        ("stop", new SlashCommandBuilder().WithName("stop").WithDescription("Stop the server (asks for confirmation).").Build()),
        ("kick", new SlashCommandBuilder().WithName("kick").WithDescription("Kick a player (asks for confirmation).")
            .AddOption("userid", ApplicationCommandOptionType.String, "Player user id (from /players)", isRequired: true)
            .AddOption("reason", ApplicationCommandOptionType.String, "Reason shown to the player", isRequired: false).Build()),
        ("ban", new SlashCommandBuilder().WithName("ban").WithDescription("Ban a player (asks for confirmation).")
            .AddOption("userid", ApplicationCommandOptionType.String, "Player user id (from /players)", isRequired: true)
            .AddOption("reason", ApplicationCommandOptionType.String, "Reason shown to the player", isRequired: false).Build()),
    };

    private async Task OnSlashCommandAsync(SocketSlashCommand command)
    {
        if (!IsAuthorized(command))
        {
            await command.RespondAsync("You're not allowed to run commands here.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!IsCommandEnabled(_config, command.CommandName) || Resolve(command.CommandName) is null)
        {
            await command.RespondAsync("That command isn't available.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (!AllowNow(command.User.Id, out var retryAfter))
        {
            await command.RespondAsync($"Slow down, try again in {retryAfter.TotalSeconds:F0}s.", ephemeral: true).ConfigureAwait(false);
            return;
        }

        var args = ExtractArgs(command);

        // Destructive commands ask first. The prompt is ephemeral, so only the invoker can click Confirm. A
        // Discord button carries no state, so the args are held in a pending entry keyed by a token in the id.
        if (DestructiveCommands.Contains(command.CommandName))
        {
            var token = NewToken();
            lock (_pendingGate)
            {
                PrunePending(DateTime.UtcNow);
                _pending[token] = new Pending(command.CommandName, args, DateTime.UtcNow);
            }
            var target = await DescribeConfirmTargetAsync(command.CommandName, args).ConfigureAwait(false);
            var buttons = new ComponentBuilder()
                .WithButton("Confirm", ButtonPrefix + token, ButtonStyle.Danger)
                .WithButton("Cancel", ButtonPrefix + "cancel", ButtonStyle.Secondary)
                .Build();
            await command.RespondAsync($"⚠️ Really **/{command.CommandName}**{target}?", components: buttons,
                ephemeral: true, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
            return;
        }

        // Post the result publicly in the channel (AllowedMentions.None so an echoed name can't ping).
        await command.DeferAsync().ConfigureAwait(false);
        var result = await RunAsync(command.CommandName, args, command.User.Username).ConfigureAwait(false);
        await command.FollowupAsync(result, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task OnButtonAsync(SocketMessageComponent component)
    {
        var id = component.Data.CustomId;
        if (!id.StartsWith(ButtonPrefix, StringComparison.Ordinal))
            return;
        var token = id[ButtonPrefix.Length..];

        if (!IsAuthorized(component))
        {
            await component.RespondAsync("You're not allowed to run commands here.", ephemeral: true).ConfigureAwait(false);
            return;
        }
        if (token == "cancel")
        {
            await ClearWith(component, "Cancelled.").ConfigureAwait(false);
            return;
        }

        Pending? pending;
        lock (_pendingGate)
            _pending.Remove(token, out pending);
        if (pending is null)
        {
            await ClearWith(component, "This confirmation expired, run the command again.").ConfigureAwait(false);
            return;
        }

        // Re-check exposure at confirm time too (defense in depth, mirrors OnSlashCommandAsync): an admin may
        // have disabled this command between the prompt and the click.
        if (!IsCommandEnabled(_config, pending.Action))
        {
            await ClearWith(component, "That command isn't available anymore.").ConfigureAwait(false);
            return;
        }

        // Clear the (ephemeral) confirm prompt for the clicker, then post the result publicly in the channel.
        await ClearWith(component, $"Confirmed, running /{pending.Action}.").ConfigureAwait(false);
        var result = await RunAsync(pending.Action, pending.Args, component.User.Username).ConfigureAwait(false);
        await component.FollowupAsync(result, allowedMentions: AllowedMentions.None).ConfigureAwait(false);
    }

    private async Task<string> RunAsync(string action, string[] args, string user)
    {
        var handler = Resolve(action);
        if (handler is null)
            return "Unknown command.";
        try
        {
            var result = await handler(args).ConfigureAwait(false);
            _logger.Info($"Discord command /{action} by {user}.");
            return result;
        }
        catch (Exception ex)
        {
            _logger.Error($"Discord command /{action} failed", ex);
            return "That command failed, check the launcher log.";
        }
    }

    private Func<string[], Task<string>>? Resolve(string name) => name switch
    {
        "status" => _ => _commands.Status(),
        "players" => _ => _commands.Players(),
        "save" => _ => _commands.Save(),
        "backup" => _ => _commands.Backup(),
        "update" => _ => _commands.Update(),
        "start" => _ => _commands.Start(),
        "restart" => _ => _commands.Restart(),
        "stop" => _ => _commands.Stop(),
        "announce" => args => _commands.Announce(Arg(args, 0)),
        "kick" => args => _commands.Kick(Arg(args, 0), Arg(args, 1)),
        "ban" => args => _commands.Ban(Arg(args, 0), Arg(args, 1)),
        "unban" => args => _commands.Unban(Arg(args, 0)),
        _ => null,
    };

    private static string Arg(string[] args, int index) => index < args.Length ? args[index] : "";

    /// <summary>For a kick/ban confirm, describe the target as " **Name** (`userid`)" so the admin sees who they're
    /// about to act on, falling back to just the id if the player isn't online. Empty for argument-less commands.</summary>
    private async Task<string> DescribeConfirmTargetAsync(string command, string[] args)
    {
        if (command is not ("kick" or "ban") || args.Length == 0)
            return "";
        var userId = args[0];
        var name = await _commands.ResolvePlayerName(userId).ConfigureAwait(false);
        return name is null ? $" `{userId}`" : $" **{name}** (`{userId}`)";
    }

    private static string[] ExtractArgs(SocketSlashCommand command) => command.CommandName switch
    {
        "announce" => new[] { Opt(command, "message") },
        "kick" => new[] { Opt(command, "userid"), Opt(command, "reason") },
        "ban" => new[] { Opt(command, "userid"), Opt(command, "reason") },
        "unban" => new[] { Opt(command, "userid") },
        _ => Array.Empty<string>(),
    };

    private static string Opt(SocketSlashCommand command, string name) =>
        command.Data.Options.FirstOrDefault(o => o.Name == name)?.Value?.ToString() ?? "";

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

    private static string NewToken() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>Drop confirm prompts nobody clicked, so the pending map can't grow without bound. Caller holds the gate.</summary>
    private void PrunePending(DateTime nowUtc)
    {
        foreach (var expired in _pending.Where(entry => nowUtc - entry.Value.CreatedUtc > PendingTtl).Select(entry => entry.Key).ToList())
            _pending.Remove(expired);
    }

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

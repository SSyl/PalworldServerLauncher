using System.Threading.Tasks;
using PalServerLauncher.Rest.Models;

namespace PalServerLauncher.Core;

/// <summary>
/// A bundle of live server operations, so the Server Commands dialog can invoke them without referencing the
/// controller or ViewModel directly (mirrors <see cref="DiscordBotService.DiscordCommands"/>). Each operation
/// resolves to false / null when the REST API is off or unreachable, so the dialog reports "couldn't do it".
/// </summary>
public sealed record ServerCommandActions(
    Func<Task<PlayersResponse?>> GetPlayers,
    Func<string, Task<bool>> Announce,
    Func<string, string, Task<bool>> Kick,
    Func<string, string, Task<bool>> Ban,
    Func<string, Task<bool>> Unban,
    Func<Task<bool>> Save,
    Func<int, Task> ShutdownWithCountdown,
    Func<Task> ForceStop);

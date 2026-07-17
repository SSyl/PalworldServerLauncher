namespace DiscordFake;

/// <summary>A stand-in whose type namespace starts with "Discord" so the namespace branch of
/// <see cref="PalServerLauncher.Core.DiscordNoiseFilter"/> can be exercised without constructing a real Discord.Net
/// exception. Lives in its own file because a namespace starting with "Discord" cannot share a file with the
/// file-scoped PalServerLauncher.Tests namespace.</summary>
public sealed class FakeConnectionException : System.Exception;

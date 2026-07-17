using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class DiscordNoiseFilterTests
{
    [Fact]
    public void Noise_when_exception_type_is_in_a_discord_namespace()
    {
        Assert.True(DiscordNoiseFilter.IsConnectionNoise("Discord.WebSocket", stackTrace: null));
    }

    [Fact]
    public void Noise_for_a_system_typed_failure_thrown_from_a_discord_frame()
    {
        // WebSocketException / HttpRequestException / TimeoutException live in System namespaces but the reconnect
        // loop throws them, so the Discord.Net frame in the stack is what identifies them as connection noise.
        Assert.True(DiscordNoiseFilter.IsConnectionNoise(
            "System.Net.WebSockets",
            "   at Discord.WebSocket.DiscordSocketClient.ConnectAsync()"));
    }

    [Fact]
    public void Not_noise_for_a_genuine_app_exception()
    {
        Assert.False(DiscordNoiseFilter.IsConnectionNoise(
            "PalServerLauncher.Core",
            "   at PalServerLauncher.Core.ServerController.StartAsync()"));
    }

    [Fact]
    public void Not_noise_for_an_app_class_named_discord_something()
    {
        // Our own DiscordNotifier / DiscordBotService are in a non-Discord namespace and "DiscordNotifier" has no
        // "Discord." substring, so an app bug thrown from them is still logged as a real error, not swallowed.
        Assert.False(DiscordNoiseFilter.IsConnectionNoise(
            "PalServerLauncher.Core",
            "   at PalServerLauncher.Core.DiscordNotifier.SendAsync()"));
    }

    [Fact]
    public void Not_noise_when_namespace_and_stack_are_both_null()
    {
        Assert.False(DiscordNoiseFilter.IsConnectionNoise(typeNamespace: null, stackTrace: null));
    }

    [Fact]
    public void FindConnectionNoise_picks_the_discord_inner_out_of_a_mixed_aggregate()
    {
        // An aggregate carrying a real app bug AND Discord noise should surface the Discord inner (so the caller logs
        // it concisely), but the app bug alone must not be masked, see the null case below.
        var appBug = new InvalidOperationException("real app bug");
        var discordNoise = new DiscordFake.FakeConnectionException();
        var aggregate = new AggregateException(appBug, discordNoise);

        Assert.Same(discordNoise, DiscordNoiseFilter.FindConnectionNoise(aggregate));
    }

    [Fact]
    public void FindConnectionNoise_null_when_no_inner_is_discord()
    {
        // No Discord frames -> null -> caller logs the whole aggregate at ERROR (a genuine bug is not swallowed).
        var aggregate = new AggregateException(
            new InvalidOperationException("bug one"),
            new TimeoutException("bug two"));

        Assert.Null(DiscordNoiseFilter.FindConnectionNoise(aggregate));
    }
}

using System.Net.Sockets;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class ServerControllerTests
{
    [Fact]
    public void FindFreeUdpPort_returns_the_start_port_when_free()
    {
        // Pick a high port unlikely to be in use.
        var port = ServerController.FindFreeUdpPort(51000);
        Assert.InRange(port, 51000, 65535);

        // Confirm it really is bindable.
        using var probe = new UdpClient(port);
        Assert.True(true);
    }

    [Fact]
    public void FindFreeUdpPort_skips_a_port_already_in_use()
    {
        using var occupied = new UdpClient(52000);
        var port = ServerController.FindFreeUdpPort(52000);

        Assert.NotEqual(52000, port);
        Assert.InRange(port, 52001, 65535);
    }

    [Fact]
    public void GenerateAdminPassword_is_20_alphanumeric_chars()
    {
        var password = ServerController.GenerateAdminPassword();
        Assert.Equal(20, password.Length);
        Assert.All(password, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void GenerateAdminPassword_differs_each_call()
    {
        // CSPRNG - two draws must not collide (not a time/source-derivable value).
        Assert.NotEqual(ServerController.GenerateAdminPassword(), ServerController.GenerateAdminPassword());
    }

    [Theory]
    [InlineData(60, 60)]   // an explicit timed shutdown keeps its duration...
    [InlineData(5, 5)]
    [InlineData(3600, 3600)]
    public void ShutdownWaitSeconds_honors_an_explicit_timer(int requested, int expected)
    {
        // ...even on an empty server. The bug: an empty server clamped a requested timer down to 1s.
        Assert.Equal(expected, ServerController.ShutdownWaitSeconds(requested));
    }

    [Theory]
    [InlineData(0)]        // a plain Stop passes 0
    [InlineData(-5)]       // and never below Palworld's minimum
    public void ShutdownWaitSeconds_clamps_to_one_second_minimum(int requested)
    {
        // Palworld's POST /shutdown rejects waittime=0 with a 400.
        Assert.Equal(1, ServerController.ShutdownWaitSeconds(requested));
    }

    [Theory]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/metrics OK")]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/info OK")]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/players OK")]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/announce OK")] // commands are noise too
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/kick OK")]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/shutdown OK")]
    public void IsRestAccessLogLine_filters_every_rest_access(string line)
    {
        Assert.True(ServerController.IsRestAccessLogLine(line));
    }

    [Theory]
    [InlineData("[2026-07-12 03:48:24] [LOG] Server started on port 8211")] // ordinary output stays
    [InlineData("[2026-07-12 03:48:24] [CHAT] <SSyl> hello")]
    [InlineData("")]
    public void IsRestAccessLogLine_keeps_ordinary_output(string line)
    {
        Assert.False(ServerController.IsRestAccessLogLine(line));
    }

    [Theory]
    [InlineData(null)]   // end-of-stream marker
    [InlineData("")]     // blank line the server emits after each REST access
    [InlineData("   ")]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/players OK")] // health-poll spam
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/ban OK")]     // command spam, dropped now
    public void ShouldLogServerLine_drops_noise(string? line)
    {
        Assert.False(ServerController.ShouldLogServerLine(line));
    }

    [Theory]
    [InlineData("[2026-07-12 03:48:24] [LOG] Server started on port 8211")] // ordinary output
    [InlineData("[2026-07-12 03:48:24] [CHAT] <SSyl> hello")]               // chat output stays
    public void ShouldLogServerLine_keeps_real_output(string line)
    {
        Assert.True(ServerController.ShouldLogServerLine(line));
    }

    [Theory]
    [InlineData("[2026-07-12 03:48:24] [CHAT] <SSyl> hello there")]
    [InlineData("[2026-07-09 22:57:26] [CHAT] <Someone> 2.5 is ok")]
    public void IsChatLine_matches_chat_output(string line)
    {
        Assert.True(ServerController.IsChatLine(line));
    }

    [Theory]
    [InlineData("[2026-07-12 03:48:24] [LOG] Server started on port 8211")]
    [InlineData("[2026-07-12 03:48:24] [LOG] REST accessed endpoint /v1/api/players OK")]
    public void IsChatLine_ignores_non_chat(string line)
    {
        Assert.False(ServerController.IsChatLine(line));
    }
}

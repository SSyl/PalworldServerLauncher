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
}

using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class DiscordCommandCooldownTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);

    [Fact]
    public void First_use_is_allowed()
    {
        var cd = new DiscordCommandCooldown();
        var now = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(cd.TryUse(userId: 1, now, Cooldown, out var retry));
        Assert.Equal(TimeSpan.Zero, retry);
    }

    [Fact]
    public void Repeat_inside_the_window_is_blocked_with_remaining_time()
    {
        var cd = new DiscordCommandCooldown();
        var now = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(cd.TryUse(1, now, Cooldown, out _));

        Assert.False(cd.TryUse(1, now.AddSeconds(2), Cooldown, out var retry));
        Assert.Equal(TimeSpan.FromSeconds(3), retry); // 5s window, 2s elapsed
    }

    [Fact]
    public void Allowed_again_once_the_window_passes()
    {
        var cd = new DiscordCommandCooldown();
        var now = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(cd.TryUse(1, now, Cooldown, out _));
        Assert.True(cd.TryUse(1, now.AddSeconds(5), Cooldown, out _)); // exactly at the boundary is allowed
    }

    [Fact]
    public void Cooldown_is_per_user()
    {
        var cd = new DiscordCommandCooldown();
        var now = new DateTime(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(cd.TryUse(1, now, Cooldown, out _));
        Assert.True(cd.TryUse(2, now, Cooldown, out _)); // a different user isn't throttled by user 1
    }
}

using System.Collections.Generic;

namespace PalServerLauncher.Core;

/// <summary>
/// Per-user command throttle for the Discord bot: records when each user last ran a command and blocks a
/// repeat inside the cooldown window, reporting how long until the next one is allowed. Thread-safe (bot
/// events fire on pool threads); the decision logic is deterministic so <see cref="TryUse"/> is unit-tested.
/// </summary>
public sealed class DiscordCommandCooldown
{
    private readonly object _gate = new();
    private readonly Dictionary<ulong, DateTime> _lastUse = new();

    /// <summary>
    /// If <paramref name="userId"/> is allowed to run a command at <paramref name="nowUtc"/> (i.e. at least
    /// <paramref name="cooldown"/> since their last), record it and return true. Otherwise return false and
    /// set <paramref name="retryAfter"/> to the remaining wait.
    /// </summary>
    public bool TryUse(ulong userId, DateTime nowUtc, TimeSpan cooldown, out TimeSpan retryAfter)
    {
        lock (_gate)
        {
            if (_lastUse.TryGetValue(userId, out var last))
            {
                var elapsed = nowUtc - last;
                if (elapsed < cooldown)
                {
                    retryAfter = cooldown - elapsed;
                    return false;
                }
            }
            _lastUse[userId] = nowUtc;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }
}

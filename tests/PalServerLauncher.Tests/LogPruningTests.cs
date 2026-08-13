using PalServerLauncher.Logging;

namespace PalServerLauncher.Tests;

/// <summary>
/// Which session logs get deleted. The pruner keeps the newest few and this decides the rest.
/// </summary>
public class LogPruningTests
{
    private static (string Path, DateTime Written) Log(string name, int minutesAgo) =>
        (name, new DateTime(2026, 8, 11, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo));

    [Fact]
    public void Nothing_is_pruned_while_under_the_keep_count()
    {
        var files = new[] { Log("a.log", 10), Log("b.log", 20) };
        Assert.Empty(Logger.SelectExpired(files, keep: 10));
    }

    [Fact]
    public void The_newest_are_kept_and_the_rest_are_returned()
    {
        var files = new[] { Log("newest.log", 1), Log("middle.log", 50), Log("oldest.log", 99) };

        var expired = Logger.SelectExpired(files, keep: 1);

        Assert.Equal(["middle.log", "oldest.log"], expired);
    }

    [Fact]
    public void A_changed_filename_format_does_not_prune_the_new_files()
    {
        // The regression this ordering exists for: an ordinal name sort puts every "launcher-2026-08-11_..."
        // before every "launcher-20260811-...", because '-' is 0x2D and '0' is 0x30. Sorting by name would
        // delete the two new logs and keep the old ones forever.
        var files = new[]
        {
            Log("launcher-20260809-031146.log", 3000),
            Log("launcher-20260810-031146.log", 2000),
            Log("launcher-2026-08-11_03.11.46.log", 60),
            Log("launcher-2026-08-11_09.11.46.log", 10),
        };

        var expired = Logger.SelectExpired(files, keep: 2);

        Assert.Equal(["launcher-20260810-031146.log", "launcher-20260809-031146.log"], expired);
    }

    [Fact]
    public void Keeping_none_returns_everything()
    {
        var files = new[] { Log("a.log", 1), Log("b.log", 2) };
        Assert.Equal(2, Logger.SelectExpired(files, keep: 0).Count);
    }
}

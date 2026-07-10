using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class PlayerRosterTests
{
    private static Dictionary<string, string> Roster(params (string id, string name)[] players) =>
        players.ToDictionary(p => p.id, p => p.name);

    [Fact]
    public void Joins_are_ids_new_in_current()
    {
        var previous = Roster(("steam_1", "Alice"));
        var current = Roster(("steam_1", "Alice"), ("steam_2", "Bob"));

        var changes = HealthMonitor.DiffRoster(previous, current);

        var change = Assert.Single(changes);
        Assert.True(change.Joined);
        Assert.Equal("Bob", change.Name);
    }

    [Fact]
    public void Leaves_are_ids_missing_from_current()
    {
        var previous = Roster(("steam_1", "Alice"), ("steam_2", "Bob"));
        var current = Roster(("steam_1", "Alice"));

        var changes = HealthMonitor.DiffRoster(previous, current);

        var change = Assert.Single(changes);
        Assert.False(change.Joined);
        Assert.Equal("Bob", change.Name);
    }

    [Fact]
    public void Simultaneous_join_and_leave_both_reported()
    {
        var previous = Roster(("steam_1", "Alice"));
        var current = Roster(("steam_2", "Bob"));

        var changes = HealthMonitor.DiffRoster(previous, current);

        Assert.Contains(changes, c => c is { Joined: true, Name: "Bob" });
        Assert.Contains(changes, c => c is { Joined: false, Name: "Alice" });
        Assert.Equal(2, changes.Count);
    }

    [Fact]
    public void No_change_yields_nothing()
    {
        var roster = Roster(("steam_1", "Alice"), ("steam_2", "Bob"));
        Assert.Empty(HealthMonitor.DiffRoster(roster, roster));
    }

    [Fact]
    public void Rename_on_same_id_is_not_a_join_or_leave()
    {
        // Same stable id, different display name -> not treated as churn.
        var previous = Roster(("steam_1", "Alice"));
        var current = Roster(("steam_1", "Alice_v2"));

        Assert.Empty(HealthMonitor.DiffRoster(previous, current));
    }
}

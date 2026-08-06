using System;
using PalServerLauncher.Core;

namespace PalServerLauncher.Tests;

public class RestartBudgetTests
{
    private static readonly DateTime T0 = new(2026, 8, 5, 20, 46, 00, DateTimeKind.Utc);

    [Fact]
    public void Allows_three_restarts_then_trips()
    {
        var budget = new RestartBudget();

        Assert.True(budget.TryConsume(T0));
        Assert.True(budget.TryConsume(T0.AddSeconds(5)));
        Assert.True(budget.TryConsume(T0.AddSeconds(10)));
        Assert.False(budget.TryConsume(T0.AddSeconds(15)));
    }

    [Fact]
    public void Stays_tripped_while_the_window_still_holds_the_old_restarts()
    {
        var budget = new RestartBudget();
        for (var i = 0; i < RestartBudget.MaxRestarts; i++)
            budget.TryConsume(T0.AddSeconds(i));

        Assert.False(budget.TryConsume(T0.AddMinutes(4)));
    }

    [Fact]
    public void Recovers_once_the_restarts_age_out_of_the_window()
    {
        var budget = new RestartBudget();
        for (var i = 0; i < RestartBudget.MaxRestarts; i++)
            budget.TryConsume(T0.AddSeconds(i));

        Assert.True(budget.TryConsume(T0 + RestartBudget.Window + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_user_start_refills_the_budget_immediately()
    {
        // Issue #11: crash x4 trips the breaker, the message says to start manually, and the user does 41s
        // later. Before the reset that Start got zero restarts, because the old window was still live.
        var budget = new RestartBudget();
        for (var i = 0; i < RestartBudget.MaxRestarts; i++)
            budget.TryConsume(T0.AddSeconds(i * 5));
        Assert.False(budget.TryConsume(T0.AddSeconds(21)));

        budget.OnStart(userInitiated: true);

        Assert.True(budget.TryConsume(T0.AddSeconds(92)));
        Assert.True(budget.TryConsume(T0.AddSeconds(97)));
        Assert.True(budget.TryConsume(T0.AddSeconds(102)));
        Assert.False(budget.TryConsume(T0.AddSeconds(107)));
    }

    [Fact]
    public void An_automatic_start_cannot_refill_its_own_budget()
    {
        var budget = new RestartBudget();
        for (var i = 0; i < RestartBudget.MaxRestarts; i++)
            budget.TryConsume(T0.AddSeconds(i));

        budget.OnStart(userInitiated: false);

        Assert.False(budget.TryConsume(T0.AddSeconds(20)));
    }

    [Fact]
    public void A_user_start_before_anything_crashed_is_harmless()
    {
        var budget = new RestartBudget();
        budget.OnStart(userInitiated: true);

        Assert.True(budget.TryConsume(T0));
    }
}

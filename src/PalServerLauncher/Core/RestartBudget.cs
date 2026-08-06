using System;
using System.Collections.Generic;

namespace PalServerLauncher.Core;

/// <summary>
/// The auto-restart circuit breaker, extracted from <see cref="ServerController"/> so its window logic is
/// unit-testable (the <see cref="RelaunchGate"/> pattern). It bounds AUTOMATIC restarts only: at most
/// <see cref="MaxRestarts"/> inside a rolling <see cref="Window"/>, after which crash relaunch and zombie
/// recovery both stand down. Not thread-safe on its own: the controller holds its lock around every call.
/// </summary>
public sealed class RestartBudget
{
    public const int MaxRestarts = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly List<DateTime> _restarts = new();

    /// <summary>Take one restart from the budget, or false when the window is already full.</summary>
    public bool TryConsume(DateTime nowUtc)
    {
        _restarts.RemoveAll(at => nowUtc - at > Window);
        if (_restarts.Count >= MaxRestarts)
            return false;
        _restarts.Add(nowUtc);
        return true;
    }

    /// <summary>
    /// A user Start (<paramref name="userInitiated"/> true) gets a fresh window, otherwise the breaker
    /// contradicts its own advice. It tells the user to fix the problem and start manually, and a stale
    /// window then re-trips on their first crash, granting zero restarts (issue #11's log shows that).
    /// A restart's own start passes false, so an automatic restart can't refill its own budget.
    /// </summary>
    public void OnStart(bool userInitiated)
    {
        if (userInitiated)
            _restarts.Clear();
    }
}

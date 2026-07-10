using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PalServerLauncher.Core;

/// <summary>
/// Drives the staged in-game restart warnings shared by every graceful restart (update / scheduled /
/// manual). Given the configured lead-minute marks (e.g., 10/5/1) and the moment the restart should
/// happen, it announces at each mark and then waits out the remainder. The largest lead mark lands at
/// the start (so "restarting in 10 minutes" fires immediately) and the smallest last.
///
/// The scheduling math (<see cref="Schedule"/>) and wording (<see cref="Message"/>) are pure and
/// unit-tested; <see cref="RunAsync"/> layers real <see cref="Task.Delay"/> waits on top of them.
/// </summary>
public static class RestartAnnouncer
{
    /// <summary>A single broadcast: how long from "now" to announce, and which lead-minute mark it is.</summary>
    public readonly record struct BroadcastMark(TimeSpan Delay, int LeadMinutes);

    /// <summary>
    /// The player-facing announcement for a reason and time-remaining, built from the user's templates.
    /// Update restarts use <paramref name="updateTemplate"/>; scheduled/manual use
    /// <paramref name="restartTemplate"/>. The token <c>{minutes}</c> is replaced with the whole minutes
    /// remaining (at least 1); a template without the token yields a fixed message with no time.
    /// </summary>
    public static string Message(RestartReason reason, TimeSpan remaining, string restartTemplate, string updateTemplate)
    {
        var template = reason == RestartReason.Update ? updateTemplate : restartTemplate;
        var minutes = Math.Max(1, (int)Math.Round(remaining.TotalMinutes));
        return template.Replace("{minutes}", minutes.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The broadcast marks for the configured leads: at most 3 distinct positive leads, largest first,
    /// each with its delay from <paramref name="now"/> (which can be zero/negative if the restart is
    /// nearer than that lead - <see cref="RunAsync"/> fires those immediately).
    /// </summary>
    public static IReadOnlyList<BroadcastMark> Schedule(IReadOnlyList<int> leadMinutes, DateTime restartAt, DateTime now) =>
        Sanitize(leadMinutes)
            .Select(lead => new BroadcastMark((restartAt - TimeSpan.FromMinutes(lead)) - now, lead))
            .ToList();

    /// <summary>
    /// Announce at each lead mark, then wait until <paramref name="restartAt"/>. <paramref name="announce"/>
    /// is invoked per mark and must not throw (failures are swallowed so a missed warning never blocks the
    /// restart); cancellation aborts the whole sequence.
    /// </summary>
    public static async Task RunAsync(
        IReadOnlyList<int> leadMinutes, DateTime restartAt, RestartReason reason,
        string restartTemplate, string updateTemplate,
        Func<string, CancellationToken, Task> announce, CancellationToken ct)
    {
        foreach (var lead in Sanitize(leadMinutes))
        {
            var wait = (restartAt - TimeSpan.FromMinutes(lead)) - DateTime.Now;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, ct).ConfigureAwait(false);

            try
            {
                await announce(Message(reason, TimeSpan.FromMinutes(lead), restartTemplate, updateTemplate), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed warning must not stop the restart from proceeding.
            }
        }

        var remaining = restartAt - DateTime.Now;
        if (remaining > TimeSpan.Zero)
            await Task.Delay(remaining, ct).ConfigureAwait(false);
    }

    /// <summary>Positive, de-duplicated, largest-first, capped at 3 - the marks we actually announce.</summary>
    private static IEnumerable<int> Sanitize(IReadOnlyList<int> leadMinutes) =>
        leadMinutes.Where(m => m > 0).Distinct().OrderByDescending(m => m).Take(3);
}

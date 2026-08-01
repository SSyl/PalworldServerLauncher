using System.Collections.Generic;
using System.Globalization;

namespace PalServerLauncher.Core;

/// <summary>How a CLI-requested stop should be carried out.</summary>
public enum StopKind
{
    /// <summary>Save and shut down immediately (the plain Stop button's behavior).</summary>
    Graceful,

    /// <summary>Save, then an in-game countdown before the shutdown lands.</summary>
    Countdown,

    /// <summary>Kill the process outright. No REST, no save.</summary>
    Kill,
}

/// <summary>
/// A stop asked for from the command line (<c>--stop-server</c> / <c>--kill-server</c>). Pure and shared by both
/// ends: the CLI process parses argv into one of these, sends it over the pipe as <see cref="ToWire"/>, and a
/// running launcher parses it back with <see cref="FromWire"/>. One type, so the two ends can't drift.
/// </summary>
public sealed record StopRequest(StopKind Kind, int Seconds = 0)
{
    /// <summary>Longest countdown accepted, matching the GUI's timed-shutdown prompt (MainWindow.PromptShutdownDecision).</summary>
    public const int MaxCountdownSeconds = 3600;

    private static readonly string[] StopFlags = ["--stop-server", "-stop-server"];
    private static readonly string[] KillFlags = ["--kill-server", "-kill-server"];

    /// <summary>
    /// Parse argv. Returns null when no stop verb is present (with <paramref name="error"/> null), or null with an
    /// <paramref name="error"/> when a verb is present but its countdown is unusable. <c>--stop-server</c> takes an
    /// optional countdown as either the next bare token or <c>=N</c>. A non-numeric next token is left alone, so
    /// <c>--stop-server --debug</c> still means an immediate stop.
    /// </summary>
    public static StopRequest? FromCommandLine(IReadOnlyList<string> args, out string? error)
    {
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (Matches(arg, KillFlags))
                return new StopRequest(StopKind.Kill);

            if (!SplitFlag(arg, StopFlags, out var inlineValue))
                continue;

            // "--stop-server=60" carries its value, otherwise a bare numeric next token is the countdown.
            var value = inlineValue ?? (i + 1 < args.Count && IsBareNumber(args[i + 1]) ? args[i + 1] : null);
            if (value is null)
                return new StopRequest(StopKind.Graceful);

            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                error = IsBareNumber(value)
                    ? $"--stop-server: countdown must be between 1 and {MaxCountdownSeconds} seconds."
                    : $"--stop-server: '{value}' is not a number of seconds.";
                return null;
            }

            // 0 or negative reads as "don't wait", which is exactly the plain graceful stop.
            if (seconds <= 0)
                return new StopRequest(StopKind.Graceful);

            if (seconds > MaxCountdownSeconds)
            {
                error = $"--stop-server: countdown must be between 1 and {MaxCountdownSeconds} seconds.";
                return null;
            }

            return new StopRequest(StopKind.Countdown, seconds);
        }

        return null;
    }

    /// <summary>The wire form sent over the pipe: "stop", "stop 60", or "kill".</summary>
    public string ToWire() => Kind switch
    {
        StopKind.Countdown => $"stop {Seconds.ToString(CultureInfo.InvariantCulture)}",
        StopKind.Kill => "kill",
        _ => "stop",
    };

    /// <summary>Parse a wire line back into a request, or null if it isn't one we recognize. Bounds are re-checked
    /// here rather than trusted: the pipe is a separate trust boundary from our own argv parse.</summary>
    public static StopRequest? FromWire(string? line)
    {
        var parts = (line ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        if (parts[0].Equals("kill", StringComparison.OrdinalIgnoreCase))
            return new StopRequest(StopKind.Kill);

        if (!parts[0].Equals("stop", StringComparison.OrdinalIgnoreCase))
            return null;

        if (parts.Length == 1)
            return new StopRequest(StopKind.Graceful);

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return null;
        if (seconds <= 0)
            return new StopRequest(StopKind.Graceful);
        if (seconds > MaxCountdownSeconds)
            return null;

        return new StopRequest(StopKind.Countdown, seconds);
    }

    private static bool Matches(string arg, string[] flags) =>
        flags.Any(f => arg.Equals(f, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if <paramref name="arg"/> is one of <paramref name="flags"/>, either bare or as "flag=value".</summary>
    private static bool SplitFlag(string arg, string[] flags, out string? value)
    {
        value = null;
        if (Matches(arg, flags))
            return true;

        var eq = arg.IndexOf('=');
        if (eq < 0 || !Matches(arg[..eq], flags))
            return false;

        value = arg[(eq + 1)..];
        return true;
    }

    /// <summary>A token that is nothing but digits (optionally signed), so a following flag is never eaten as a
    /// value. Deliberately not an int.TryParse: a value too large for an int has to reach the range error, not
    /// be discarded as "not a number" and silently turn the request into an immediate stop.</summary>
    private static bool IsBareNumber(string arg)
    {
        var digits = arg.StartsWith('-') || arg.StartsWith('+') ? arg[1..] : arg;
        return digits.Length > 0 && digits.All(char.IsAsciiDigit);
    }
}

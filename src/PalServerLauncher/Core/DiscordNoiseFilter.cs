using System;

namespace PalServerLauncher.Core;

/// <summary>
/// Decides whether an unobserved background-task exception is transient Discord.Net connection noise (its reconnect
/// loop throws connect/HTTP/WebSocket failures into background tasks that self-heal on retry) rather than a genuine
/// app bug. Not all of those failures live in a Discord namespace, some are System-typed WebSocket/HTTP/timeout
/// exceptions, so we also look for a Discord.Net frame in the stack. Pure and unit-tested.
/// </summary>
public static class DiscordNoiseFilter
{
    /// <summary>Return the first inner exception that is Discord connection noise, or null when none are (so a genuine
    /// app bug with no Discord frames still gets logged at ERROR).</summary>
    public static Exception? FindConnectionNoise(AggregateException aggregate)
    {
        foreach (var inner in aggregate.Flatten().InnerExceptions)
            if (IsConnectionNoise(inner))
                return inner;
        return null;
    }

    /// <summary>True when the exception's type lives in a Discord namespace, or a Discord.Net frame appears in its
    /// stack (catching the System-typed WebSocket/HTTP/timeout failures the reconnect loop also leaks).</summary>
    public static bool IsConnectionNoise(Exception inner) =>
        IsConnectionNoise(inner.GetType().Namespace, inner.StackTrace);

    /// <summary>Testable core: classify by the exception's type namespace and stack-trace text.</summary>
    public static bool IsConnectionNoise(string? typeNamespace, string? stackTrace) =>
        (typeNamespace?.StartsWith("Discord", StringComparison.Ordinal) == true)
        || (stackTrace?.Contains("Discord.", StringComparison.Ordinal) == true);
}

using System.Globalization;

namespace PalServerLauncher.Logging;

/// <summary>
/// Strips the timestamp a source stamped onto its own output, so a UI line carries one time instead of two.
/// SteamCMD and Palworld both write <c>[2026-08-12 14:57:39]</c>, and Palworld's is worth keeping, because its
/// player and chat lines reach us in bursts up to a minute late, so its own stamp is the real event time while
/// ours is only when we read the line. Unreal's <c>[2026.08.12-19.57.56:668][436]</c> is UTC and matched our
/// local time exactly once converted, so that one is dropped rather than parsed.
/// </summary>
public static class LogLinePrefix
{
    /// <summary>
    /// Split a source-stamped prefix off <paramref name="text"/>. Returns the time the source recorded when it
    /// wrote one we trust, and the remaining text. Anchored at position 0 and never searched for, because chat
    /// lines carry player-authored text and a message that merely looks like a timestamp must not count as one.
    /// </summary>
    public static (DateTime? Time, string Text) Split(string text)
    {
        if (string.IsNullOrEmpty(text) || text[0] != '[')
            return (null, text);

        var close = text.IndexOf(']');
        if (close < 0)
            return (null, text);

        var inner = text[1..close];

        if (DateTime.TryParseExact(inner, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var stamped))
            return (stamped, text[(close + 1)..].TrimStart());

        // Unreal's own prefix, always followed by a [frame] counter. Dropped without its time, which is UTC.
        if (DateTime.TryParseExact(inner, "yyyy.MM.dd-HH.mm.ss:fff", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out _))
        {
            var rest = text[(close + 1)..];
            if (rest.StartsWith('[') && rest.IndexOf(']') is var frameEnd and >= 0)
                rest = rest[(frameEnd + 1)..];
            return (null, rest.TrimStart());
        }

        return (null, text);
    }

    /// <summary>
    /// Drop the markers a source stamps on its own chat lines, since the Chat tab and the CHAT tag already say
    /// what the line is. Vanilla writes <c>[CHAT] &lt;name&gt; text</c>. PalDefender replaces the line wholesale
    /// with <c>[20:03:50][info] [Chat::Global]['name' (UserId=...)]: text</c>, so its time and level go too.
    /// Its channel is KEPT and shortened to <c>[Global]</c>, because vanilla Palworld marks no channel at all
    /// and throwing it away would leave a PalDefender user worse off than the format they installed it for.
    /// Its time is discarded rather than used, because it carries no date. Only known markers are removed and
    /// the first unrecognized bracket ends the scan, which stops a bracketed player name being eaten as metadata.
    /// </summary>
    public static string StripChatMarkers(string text)
    {
        var channel = "";
        var rest = text.AsSpan();
        while (rest.Length > 0 && rest[0] == '[')
        {
            var close = rest.IndexOf(']');
            if (close < 0)
                break;

            var token = rest[1..close];
            if (token.StartsWith("Chat::", StringComparison.OrdinalIgnoreCase))
            {
                var scope = token[6..].Trim();
                if (!scope.IsEmpty)
                    channel = $"[{scope}] ";
            }
            else if (!IsMarker(token))
                break;

            rest = rest[(close + 1)..].TrimStart();
        }
        return channel + rest.ToString();
    }

    private static bool IsMarker(ReadOnlySpan<char> token) =>
        token.Equals("CHAT", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("info", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("warn", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("error", StringComparison.OrdinalIgnoreCase) ||
        TimeOnly.TryParseExact(token, "HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}

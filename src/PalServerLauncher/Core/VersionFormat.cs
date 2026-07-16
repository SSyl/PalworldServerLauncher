using System;
using System.Linq;

namespace PalServerLauncher.Core;

/// <summary>Formatting for the game version string reported by the REST <c>/info</c> endpoint.</summary>
public static class VersionFormat
{
    /// <summary>
    /// Truncate a Palworld <c>/info</c> version like <c>v1.0.0.100427</c> to <c>v1.0.0</c> (major.minor.patch).
    /// Returns null for values that aren't a dotted-numeric version, including the health sample's <c>-</c> and
    /// <c>REST off</c> sentinels, so callers can fall back to the raw text or a build id. Version-agnostic: it
    /// keeps whatever leading numeric segments are present (up to three), so a future <c>v2.1</c> or <c>v1</c>
    /// passes through sensibly.
    /// </summary>
    public static string? ShortVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = raw.Trim();
        var body = text.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? text[1..] : text;
        var numeric = body.Split('.')
            .TakeWhile(segment => segment.Length > 0 && segment.All(char.IsAsciiDigit))
            .Take(3)
            .ToArray();

        return numeric.Length == 0 ? null : "v" + string.Join('.', numeric);
    }

    /// <summary>
    /// A build's display label. With a known short version: <c>v1.0.1 (24181105)</c>. Without one: the build id
    /// run through <paramref name="buildOnlyFormat"/> (the localized "build {0}"). An empty build id shows as
    /// <c>?</c> so the label is never blank.
    /// </summary>
    public static string Label(string? shortVersion, string? buildId, string buildOnlyFormat)
    {
        var build = string.IsNullOrEmpty(buildId) ? "?" : buildId;
        return string.IsNullOrEmpty(shortVersion)
            ? string.Format(buildOnlyFormat, build)
            : $"{shortVersion} ({build})";
    }
}

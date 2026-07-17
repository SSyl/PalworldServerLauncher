using System.Globalization;

namespace PalServerLauncher.Core;

/// <summary>Formatting for the server process's memory use (working set), shown in the status tile and the
/// Discord <c>/status</c> command. Below 1 GB it reads in whole MB (e.g. <c>512 MB</c>), at or above 1 GB in
/// GB to two decimals (e.g. <c>1.50 GB</c>).</summary>
public static class MemoryFormat
{
    /// <summary>Format working-set bytes. Defaults to the invariant culture (a dot decimal) for the Discord
    /// <c>/status</c> text and deterministic tests. Pass <see cref="CultureInfo.CurrentCulture"/> for the in-app
    /// tile so the decimal separator matches the user's regional format.</summary>
    public static string Format(long bytes, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.InvariantCulture;
        var mb = (bytes < 0 ? 0 : bytes) / 1024d / 1024d;
        return mb >= 1024
            ? string.Format(culture, "{0:F2} GB", mb / 1024)
            : string.Format(culture, "{0:F0} MB", mb);
    }
}

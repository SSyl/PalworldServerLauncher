using System.Globalization;

namespace PalServerLauncher.Core;

/// <summary>Formatting for the server process's memory use (working set), shown in the status tile and the
/// Discord <c>/status</c> command. Below 1 GB it reads in whole MB (e.g. <c>512 MB</c>), at or above 1 GB in
/// GB to two decimals (e.g. <c>1.50 GB</c>). Uses the invariant culture so the value is deterministic and the
/// decimal point stays a dot regardless of the machine's regional format.</summary>
public static class MemoryFormat
{
    public static string Format(long bytes)
    {
        var mb = (bytes < 0 ? 0 : bytes) / 1024d / 1024d;
        return mb >= 1024
            ? string.Format(CultureInfo.InvariantCulture, "{0:F2} GB", mb / 1024)
            : string.Format(CultureInfo.InvariantCulture, "{0:F0} MB", mb);
    }
}

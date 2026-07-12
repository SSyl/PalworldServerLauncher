using System.Linq;

namespace PalServerLauncher.Core;

/// <summary>
/// Extracts a Steam Workshop id from user input: a bare digit string, or a pasted
/// <c>steamcommunity.com/sharedfiles/filedetails/?id=NNN</c> URL. Returns the digits, or null when
/// there's no valid id. Pure, so it's unit-tested.
/// </summary>
public static class WorkshopId
{
    public static string? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;
        var text = input.Trim();

        // A URL carries the id in "?id=NNN" (with optional trailing &params).
        var marker = text.IndexOf("id=", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            var digits = new string(text[(marker + 3)..].TakeWhile(char.IsAsciiDigit).ToArray());
            return digits.Length > 0 ? digits : null;
        }

        // Otherwise it must be a bare numeric id.
        return text.All(char.IsAsciiDigit) ? text : null;
    }
}
